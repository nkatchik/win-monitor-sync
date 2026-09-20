using System.Diagnostics;
using MonitorSync.Core;
using MonitorSync.Windows;

namespace MonitorSync.App;

public sealed class SyncController : IDisposable
{
    public static string BuildVersion => typeof(SyncController).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    private readonly DdcClient? _ddc;
    private readonly IVolumeEnvironment _environment;
    private readonly bool _ownsDdc;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _connection;
    private Task _runTask = Task.CompletedTask;
    private HardwareVolumeController? _engine;
    private IAudioConnection? _audio;
    private string? _monitorId;
    private bool _connectionInitialized;
    private bool _started, _suspended, _disposed;
    private string _status = "Finding monitor speakers…", _levels = "";

    public event EventHandler? Changed;
    public string Status => _status;
    public string Levels => _levels;
    public bool CanControlVolume => !_disposed && !_lifetime.IsCancellationRequested && !_suspended && _connection?.IsCancellationRequested == false &&
        _audio?.IsCurrentRoute == true && _engine is not null;
    public int? VolumePercent => CanControlVolume ? _engine!.MonitorPercent : null;
    public bool IsPending => CanControlVolume && _engine!.IsPending;

    public bool SetVolumePercent(int percent)
    {
        if (!CanControlVolume) return false;
        try
        {
            _engine!.SetPercent(percent);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception error)
        {
            _environment.Log(error.ToString());
            TopologyChanged();
            return false;
        }
    }

    public SyncController(DdcClient? client = null)
    {
        _ddc = client ?? new DdcClient();
        _ownsDdc = client is null;
        _environment = new WindowsVolumeEnvironment(_ddc);
    }

    internal SyncController(IVolumeEnvironment environment) => _environment = environment;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
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
                    var output = _environment.DescribeDefault();
                    if (_audio is not null && (!_audio.IsCurrentRoute || _audio.Capture().EndpointId != output.Id))
                        ResetAudio();
                    if (!output.IsDisplayAudio)
                    {
                        ResetAudio();
                        SetStatus("No monitor speakers selected");
                    }
                    else
                    {
                        var monitors = await _environment.ListAsync(token);
                        token.ThrowIfCancellationRequested();
                        var monitor = AutomaticMonitorSelection.Find(output.MonitorName, output.IsDisplayAudio, monitors);
                        if (monitor is null)
                        {
                            SetStatus("Waiting for a supported monitor — check DDC/CI");
                            retryDelay = 2000;
                        }
                        else
                        {
                            if (!string.Equals(_monitorId, monitor.Id, StringComparison.OrdinalIgnoreCase)) ResetAudio();
                            _audio ??= _environment.OpenAudio(output.Id);
                            _monitorId = monitor.Id;
                            var clock = Stopwatch.StartNew();
                            var engine = new HardwareVolumeController(_audio, _environment.OpenMonitor(monitor.Id, output.Id),
                                () => clock.ElapsedMilliseconds);
                            await engine.StartAsync(token, preserveWindowsVolume: _connectionInitialized);
                            _connectionInitialized = true;
                            _engine = engine;
                            while (true)
                            {
                                await engine.TickAsync(token);
                                token.ThrowIfCancellationRequested();
                                SetStatus(engine.IsPending ? "Adjusting monitor volume…" : "Monitor volume control is on",
                                    $"Volume {engine.MonitorPercent}%");
                                await Task.Delay(20, token);
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
                ResetAudio();
                SetStatus("Checking playback output…");
            }
            catch (MonitorTargetChangedException)
            {
                ResetAudio();
                SetStatus("Checking playback output…");
                retryDelay = 0;
            }
            catch (Exception error)
            {
                SetStatus("Monitor unavailable — retrying automatically");
                _environment.Log(error.ToString());
                retryDelay = 1000;
            }
            finally
            {
                _engine = null;
                // Keep the audio subscription across DDC failures so an output switch,
                // even away and back during a retry, invalidates recovery on this route.
                if (!_connectionInitialized || _audio?.IsCurrentRoute != true) ResetAudio();
                // Publish loss of availability after clearing the failed connection,
                // including when brightness polling is disabled.
                Changed?.Invoke(this, EventArgs.Empty);
            }

            try { await Task.Delay(retryDelay, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            _connection = null;
        }
        ResetAudio();
    }

    public void TopologyChanged()
    {
        _connectionInitialized = false;
        _connection?.Cancel();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        TopologyChanged();
    }

    private void ResetAudio()
    {
        _audio?.Dispose();
        _audio = null;
        _monitorId = null;
        _connectionInitialized = false;
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
        ResetAudio();
        if (_ownsDdc) _ddc?.Dispose();
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
