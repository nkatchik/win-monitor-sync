using System.Diagnostics;
using MonitorSync.Core;
using MonitorSync.Windows;

namespace MonitorSync.App;

public sealed class SyncController : IDisposable
{
    public static string BuildVersion => typeof(SyncController).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    private readonly DdcClient _ddc;
    private readonly bool _ownsDdc;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _connection;
    private Task _runTask = Task.CompletedTask;
    private HardwareVolumeController? _engine;
    private AudioEndpoint? _audio;
    private int _muteRequests;
    private bool _started, _suspended, _disposed;
    private string _status = "Finding monitor speakers…", _levels = "";

    public event EventHandler? Changed;
    public string Status => _status;
    public string Levels => _levels;
    public bool CanControlVolume => !_suspended && _connection?.IsCancellationRequested == false &&
        _audio?.IsCurrentRoute == true && _engine is not null;
    public int? VolumePercent => CanControlVolume ? _engine!.MonitorPercent : null;
    public bool IsPending => CanControlVolume && _engine!.IsPending;

    public bool SetVolumePercent(int percent)
    {
        if (!CanControlVolume) return false;
        _engine!.SetPercent(percent);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryQueueVolumeKey(int delta)
    {
        if (!CanControlVolume) return false;
        if (delta == 0) _muteRequests++; else _engine!.Step(delta);
        return true;
    }

    public SyncController(DdcClient? client = null)
    {
        _ddc = client ?? new DdcClient();
        _ownsDdc = client is null;
    }

    public void Start(bool volumeKeysAvailable = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        if (!volumeKeysAvailable) { SetStatus("Volume keys unavailable — using Windows volume"); return; }
        _started = true;
        _runTask = RunAsync(_lifetime.Token);
    }

    // Lifecycle calls and continuations run on the application's dispatcher.
    // One loop owns discovery, audio subscriptions, and DDC requests.
    private async Task RunAsync(CancellationToken lifetime)
    {
        while (!lifetime.IsCancellationRequested)
        {
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _connection = connection;
            var token = connection.Token;
            var retryDelay = 2000;
            try
            {
                if (_suspended)
                {
                    SetStatus("Waiting for Windows to resume");
                }
                else
                {
                    var output = AudioEndpoint.DescribeDefault();
                    if (!output.IsDisplayAudio)
                    {
                        SetStatus("No monitor speakers selected");
                    }
                    else
                    {
                        var monitors = await _ddc.ListAsync(token);
                        token.ThrowIfCancellationRequested();
                        var monitor = AutomaticMonitorSelection.Find(output.MonitorName, output.IsDisplayAudio, monitors);
                        if (monitor is null)
                        {
                            SetStatus("Waiting for a supported monitor — check DDC/CI");
                            retryDelay = 2000;
                        }
                        else
                        {
                            using var audio = new AudioEndpoint(output.Id);
                            var clock = Stopwatch.StartNew();
                            var engine = new HardwareVolumeController(audio, new MonitorVolume(_ddc, monitor.Id, output.Id),
                                () => clock.ElapsedMilliseconds);
                            await engine.StartAsync(token);
                            _audio = audio;
                            _engine = engine;
                            while (true)
                            {
                                if (_muteRequests > 0)
                                {
                                    if ((_muteRequests & 1) != 0) audio.TryToggleMute(audio.Capture());
                                    _muteRequests = 0;
                                }
                                await engine.TickAsync(token);
                                token.ThrowIfCancellationRequested();
                                SetStatus(engine.IsPending ? "Adjusting monitor volume…" : "Monitor volume control is on",
                                    $"Volume {engine.MonitorPercent}%");
                                await Task.Delay(50, token);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                SetStatus("Checking monitor connection…");
                retryDelay = 0;
            }
            catch (AudioRouteChangedException)
            {
                SetStatus("Checking playback output…");
            }
            catch (MonitorTargetChangedException)
            {
                SetStatus("Checking playback output…");
                retryDelay = 0;
            }
            catch (Exception error)
            {
                SetStatus("Monitor unavailable — retrying automatically");
                SettingsStore.Log(error.ToString());
                retryDelay = 1000;
            }
            finally
            {
                _engine = null;
                _audio = null;
                _muteRequests = 0;
            }

            try { await Task.Delay(retryDelay, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            _connection = null;
        }
    }

    public void TopologyChanged() => _connection?.Cancel();

    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        TopologyChanged();
    }

    private void SetStatus(string status, string levels = "")
    {
        if (_status == status && _levels == levels) return;
        _status = status;
        _levels = levels;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task CloseAsync()
    {
        if (_disposed) return;
        _lifetime.Cancel();
        await _runTask;
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_ownsDdc) _ddc.Dispose();
        _lifetime.Dispose();
    }

    public static async Task<object> CreateReportAsync(DdcClient client, CancellationToken token = default)
    {
        object audio;
        try { audio = AudioEndpoint.DescribeDefault(); }
        catch (Exception error) { audio = new { Error = error.Message }; }
        var monitors = await client.ListAsync(token);
        return new { Version = BuildVersion, CapturedAt = DateTimeOffset.UtcNow,
            OS = Environment.OSVersion.VersionString, Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            Audio = audio, Monitors = monitors, BrightnessInputs = HidBrightnessInput.DescribeDevices(),
            Note = "Read-only report. DDC support/readback is not proof of successful hardware adjustment." };
    }
}
