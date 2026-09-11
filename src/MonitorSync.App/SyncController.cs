using System.Diagnostics;
using MonitorSync.Core;
using MonitorSync.Windows;

namespace MonitorSync.App;

public sealed class SyncController : IDisposable
{
    public static string BuildVersion => typeof(SyncController).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    private readonly DdcClient _ddc = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _connection;
    private Task _runTask = Task.CompletedTask;
    private bool _started, _suspended, _disposed;
    private string _status = "Finding monitor speakers…", _levels = "";

    public event EventHandler? Changed;
    public string Status => _status;
    public string Levels => _levels;

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
                    var output = AudioEndpoint.DescribeDefault();
                    if (!output.IsDisplayAudio)
                    {
                        SetStatus("Waiting for monitor speakers");
                    }
                    else
                    {
                        var monitors = await _ddc.ListAsync(token);
                        token.ThrowIfCancellationRequested();
                        var monitor = AutomaticMonitorSelection.Find(output.MonitorName, output.IsDisplayAudio, monitors);
                        if (monitor is null)
                        {
                            SetStatus("Waiting for a supported monitor — check DDC/CI");
                            retryDelay = 10000;
                        }
                        else
                        {
                            using var audio = new AudioEndpoint(output.Id);
                            var clock = Stopwatch.StartNew();
                            var engine = new VolumeSynchronizer(audio, new MonitorVolume(_ddc, monitor.Id),
                                () => clock.ElapsedMilliseconds);
                            await engine.StartAsync(token);
                            while (true)
                            {
                                await engine.TickAsync(token);
                                token.ThrowIfCancellationRequested();
                                SetStatus(engine.IsPending ? "Syncing volume…" : "Volume sync is on",
                                    $"Windows {engine.WindowsPercent}% · Monitor {engine.MonitorPercent}%");
                                await Task.Delay(100, token);
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
            catch (Exception error)
            {
                SetStatus("Monitor unavailable — retrying automatically");
                SettingsStore.Log(error.ToString());
                retryDelay = 10000;
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
        _ddc.Dispose();
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
            Audio = audio, Monitors = monitors,
            Note = "Read-only report. DDC support/readback is not proof of successful hardware adjustment." };
    }
}
