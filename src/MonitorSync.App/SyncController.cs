using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using MonitorSync.Core;
using MonitorSync.Windows;

namespace MonitorSync.App;

public sealed class SyncController : INotifyPropertyChanged
{
    public static string BuildVersion => typeof(SyncController).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    public string VersionText => $"Closing this window keeps sync running in the notification area. Version {BuildVersion} preview.";
    private readonly DdcClient _ddc = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _reconnect = new() { Interval = TimeSpan.FromSeconds(2) };
    private UserSettings _settings = SettingsStore.Load();
    private CancellationTokenSource? _operation, _runtimeCancellation;
    private Task _runtimeTask = Task.CompletedTask;
    private bool _busy, _running, _waiting, _closing;
    private MonitorDescriptor? _selected;
    private string _status = "Reading devices…", _audioName = "No playback device", _levels = "";
    private double _brightness;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<MonitorDescriptor> Monitors { get; } = [];
    public bool CanInteract => !_busy;
    public bool CanSelect => !_busy && !_running;
    public bool CanEnable => !_busy && !_running && _selected?.Volume is not null;
    public bool CanAdjustBrightness => !_busy && _selected?.Brightness is not null;
    public bool IsRunning => _running;
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string AudioName { get => _audioName; private set { _audioName = value; Changed(); } }
    public string Levels { get => _levels; private set { _levels = value; Changed(); } }
    public string MonitorDetails => _selected is null ? "Select a monitor with DDC/CI enabled." :
        $"Volume: {(_selected.Volume.HasValue ? "available" : "unavailable")}. " +
        $"Brightness: {(_selected.Brightness.HasValue ? "available" : "unavailable")}." +
        (_selected.Error is null ? "" : " " + _selected.Error);
    public MonitorDescriptor? SelectedMonitor
    {
        get => _selected;
        set
        {
            _selected = value; Changed(); Changed(nameof(MonitorDetails));
            Brightness = value?.Brightness?.Percent ?? 0; UpdateControls();
        }
    }
    public double Brightness { get => _brightness; set { _brightness = value; Changed(); } }
    public bool StartWithWindows
    {
        get => SettingsStore.StartsWithWindows;
        set
        {
            try { SettingsStore.StartsWithWindows = value; }
            catch (Exception e) { Status = "Could not update startup: " + e.Message; SettingsStore.Log(e.ToString()); }
            Changed();
        }
    }

    public SyncController()
    {
        _reconnect.Tick += async (_, _) =>
        {
            if (_closing || _busy || _running || !_waiting || !_settings.SyncEnabled) return;
            try
            {
                if (AudioEndpoint.DescribeDefault().Id == _settings.EndpointId)
                    await ExecuteAsync(StartSavedAsync);
            }
            catch (Exception e) when (e is IOException or System.Runtime.InteropServices.COMException) { }
        };
    }

    public Task InitializeAsync() => ExecuteAsync(async token =>
    {
        _reconnect.Start();
        await ScanAsync(token);
        if (_settings.SyncEnabled) await StartSavedAsync(token);
        else Status = "Ready to pair. Choose the monitor that plays this output's audio.";
    });

    public Task RefreshAsync() => ExecuteAsync(async token =>
    {
        await StopRuntimeAsync();
        _settings = _settings with { SyncEnabled = false };
        SettingsStore.Save(_settings);
        await ScanAsync(token);
        Status = "Devices refreshed. Enable sync to confirm this pairing.";
    });

    private async Task ScanAsync(CancellationToken token)
    {
        Status = "Reading monitor controls…";
        var monitors = await _ddc.ListAsync(token);
        token.ThrowIfCancellationRequested();
        Monitors.Clear(); foreach (var item in monitors) Monitors.Add(item);
        SelectedMonitor = monitors.SingleOrDefault(m => m.Id == _settings.MonitorId)
            ?? (monitors.Length == 1 ? monitors[0] : null);
        try
        {
            var output = AudioEndpoint.DescribeDefault();
            AudioName = output.Name;
            Levels = $"Windows {output.Percent}%{(output.Muted ? " (muted)" : "")}";
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or AudioRouteChangedException)
        { AudioName = "No default playback device is available"; Levels = ""; }
    }

    public Task EnableAsync() => ExecuteAsync(async token =>
    {
        if (_selected?.Volume is null) throw new IOException("This monitor does not report readable volume control.");
        var output = AudioEndpoint.DescribeDefault();
        _settings = new(_selected.Id, output.Id, true);
        SettingsStore.Save(_settings);
        await StartSavedAsync(token);
    });

    private async Task StartSavedAsync(CancellationToken token)
    {
        await StopRuntimeAsync();
        if (_settings.EndpointId is null || _settings.MonitorId is null)
            throw new IOException("Choose a monitor and enable sync to create a pairing.");
        var output = AudioEndpoint.DescribeDefault();
        AudioName = output.Name;
        if (output.Id != _settings.EndpointId)
        {
            _waiting = true;
            Status = "Waiting for the paired playback output. This output is left unchanged.";
            return;
        }
        Status = "Checking the saved monitor pairing…";
        var monitors = await _ddc.ListAsync(token);
        token.ThrowIfCancellationRequested();
        var paired = monitors.SingleOrDefault(m => m.Id == _settings.MonitorId);
        if (paired?.Volume is null) throw new IOException("The paired monitor is unavailable or cannot read volume. Refresh to retry.");
        SelectedMonitor = Monitors.FirstOrDefault(m => m.Id == paired.Id) ?? paired;
        var audio = new AudioEndpoint(output.Id);
        try
        {
            var engine = new VolumeSynchronizer(audio, new MonitorVolume(_ddc, paired.Id), () => _clock.ElapsedMilliseconds);
            await engine.StartAsync(token);
            token.ThrowIfCancellationRequested();
            _runtimeCancellation = new();
            _waiting = false;
            _running = true;
            UpdateControls();
            _runtimeTask = RunAsync(engine, audio, _runtimeCancellation.Token);
        }
        catch { audio.Dispose(); throw; }
    }

    private async Task RunAsync(VolumeSynchronizer engine, AudioEndpoint audio, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await engine.TickAsync(token);
                token.ThrowIfCancellationRequested();
                Status = engine.IsPending ? "Applying monitor volume…" : "Volume sync is on";
                Levels = $"Windows {engine.WindowsPercent}%  ·  Monitor {engine.MonitorPercent}%";
                await Task.Delay(100, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (AudioRouteChangedException)
        {
            _waiting = true;
            Status = "Waiting for the paired playback output. This output is left unchanged.";
        }
        catch (Exception e)
        {
            _waiting = false;
            _settings = _settings with { SyncEnabled = false };
            SaveWithoutThrowing();
            Status = "Sync paused: " + e.Message;
            SettingsStore.Log(e.ToString());
        }
        finally { audio.Dispose(); _running = false; UpdateControls(); }
    }

    public async Task PauseAsync()
    {
        _operation?.Cancel();
        _waiting = false;
        _settings = _settings with { SyncEnabled = false };
        SaveWithoutThrowing();
        await StopRuntimeAsync();
        Status = "Sync is paused. Windows audio remains available.";
    }

    public async Task TopologyChangedAsync()
    {
        if (_closing) return;
        _operation?.Cancel();
        await StopRuntimeAsync();
        _waiting = _settings.SyncEnabled;
        Status = "Display connection changed. Rechecking the saved pairing…";
    }

    public Task ApplyBrightnessAsync() => ExecuteAsync(async token =>
    {
        if (_selected?.Brightness is not VolumeReading range) throw new IOException("Brightness control is unavailable.");
        var id = _selected.Id;
        var desired = range.RawFor((int)Math.Round(Brightness));
        await _ddc.WriteAsync(id, 0x10, desired, token);
        await Task.Delay(200, token);
        var actual = await _ddc.ReadAsync(id, 0x10, token);
        token.ThrowIfCancellationRequested();
        Brightness = actual.Percent;
        if (actual.Current != desired) throw new IOException("The monitor did not apply brightness. Check HDR mode and DDC/CI.");
        Status = $"Monitor brightness is {actual.Percent}%.";
    });

    public Task SaveDiagnosticsAsync(string path) => ExecuteAsync(async token =>
    {
        // Use a different worker so diagnostic enumeration cannot invalidate handles
        // being used by an active synchronization operation.
        using var diagnosticClient = new DdcClient();
        var report = await CreateReportAsync(diagnosticClient, token);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }), token);
        Status = "Diagnostic report saved.";
    });

    public static async Task<object> CreateReportAsync(DdcClient client, CancellationToken token = default)
    {
        object audio;
        try { audio = AudioEndpoint.DescribeDefault(); }
        catch (Exception e) { audio = new { Error = e.Message }; }
        var monitors = await client.ListAsync(token);
        return new { Version = BuildVersion, CapturedAt = DateTimeOffset.UtcNow,
            OS = Environment.OSVersion.VersionString, Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            Audio = audio, Monitors = monitors,
            Note = "Read-only report. DDC support/readback is not proof of successful hardware adjustment." };
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || _closing) return;
        _busy = true; UpdateControls();
        using var operation = new CancellationTokenSource();
        _operation = operation;
        try { await action(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (AudioRouteChangedException e)
        {
            await StopRuntimeAsync();
            _waiting = _settings.SyncEnabled;
            Status = e.Message + " Waiting for the paired output.";
        }
        catch (Exception e)
        {
            await StopRuntimeAsync();
            _waiting = false;
            _settings = _settings with { SyncEnabled = false };
            SaveWithoutThrowing();
            Status = e.Message;
            SettingsStore.Log(e.ToString());
        }
        finally { _operation = null; _busy = false; UpdateControls(); }
    }

    private async Task StopRuntimeAsync()
    {
        _runtimeCancellation?.Cancel();
        await _runtimeTask;
        _runtimeCancellation?.Dispose();
        _runtimeCancellation = null;
    }

    public async Task CloseAsync()
    {
        _closing = true;
        _reconnect.Stop();
        _operation?.Cancel();
        await StopRuntimeAsync();
        _ddc.Dispose();
    }

    private void SaveWithoutThrowing()
    {
        try { SettingsStore.Save(_settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SettingsStore.Log(e.ToString()); }
    }
    private void UpdateControls()
    {
        Changed(nameof(CanInteract)); Changed(nameof(CanSelect)); Changed(nameof(CanEnable));
        Changed(nameof(CanAdjustBrightness)); Changed(nameof(IsRunning));
    }
    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
