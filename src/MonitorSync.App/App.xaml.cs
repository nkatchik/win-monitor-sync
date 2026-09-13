using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace MonitorSync.App;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    private Forms.NotifyIcon? _tray;
    private TrayFlyout? _flyout;
    private SyncController? _controller;
    private DdcClient? _ddc;
    private BrightnessController? _brightness;
    private BrightnessHotkeys? _brightnessHotkeys;
    private BrightnessMediaKeys? _brightnessMediaKeys;
    private VolumeMediaKeys? _volumeMediaKeys;
    private bool _brightnessActive, _volumeActive, _suspended, _exiting, _updatingPreferences;
    private Task _applyTask = Task.CompletedTask;
    private Task _brightnessStopTask = Task.CompletedTask, _volumeStopTask = Task.CompletedTask;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            SettingsStore.Log(args.Exception.ToString());
            args.Handled = true;
        };
        if (e.Args is ["--diagnostics", var path])
        {
            try
            {
                using var client = new DdcClient();
                var report = await SyncController.CreateReportAsync(client);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
            catch (Exception error) { SettingsStore.Log(error.ToString()); Shutdown(1); }
            return;
        }

        _instance = new Mutex(true, @"Local\MonitorSync.SingleInstance", out var created);
        if (!created) { Shutdown(); return; }

        _flyout = new TrayFlyout();
        async void ActiveChanged(object sender, RoutedEventArgs args)
        {
            if (_updatingPreferences || _exiting) return;
            var brightness = ReferenceEquals(sender, _flyout.Brightness.Active);
            if (brightness) _brightnessActive = _flyout.Brightness.Active.IsChecked == true;
            else _volumeActive = _flyout.Volume.Active.IsChecked == true;
            try
            {
                if (brightness) SettingsStore.BrightnessActive = _brightnessActive;
                else SettingsStore.VolumeActive = _volumeActive;
            }
            catch (Exception error) { SettingsStore.Log(error.ToString()); }
            try { await ApplyMonitoringAsync(); }
            catch (Exception error) { SettingsStore.Log(error.ToString()); }
        }
        _flyout.Brightness.Active.Checked += ActiveChanged;
        _flyout.Brightness.Active.Unchecked += ActiveChanged;
        _flyout.Volume.Active.Checked += ActiveChanged;
        _flyout.Volume.Active.Unchecked += ActiveChanged;
        try { SettingsStore.InitializeStartup(); }
        catch (Exception error) { SettingsStore.Log(error.ToString()); }
        UpdateStartupItem();
        void StartupChanged(object sender, RoutedEventArgs args)
        {
            if (_updatingPreferences || _exiting) return;
            try { SettingsStore.StartsWithWindows = _flyout.StartupItem.IsChecked == true; }
            catch (Exception error) { SettingsStore.Log(error.ToString()); }
            UpdateStartupItem();
        }
        _flyout.StartupItem.Checked += StartupChanged;
        _flyout.StartupItem.Unchecked += StartupChanged;

        _flyout.ExitItem.Click += async (_, _) => await ExitAsync();
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Monitor Sync", Visible = true
        };
        _tray.MouseUp += (_, args) =>
        {
            if (_exiting || args.Button is not (Forms.MouseButtons.Left or Forms.MouseButtons.Right)) return;
            UpdateStartupItem();
            _flyout.ShowAtCursor();
        };
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        _brightnessActive = ReadActive(() => SettingsStore.BrightnessActive);
        _volumeActive = ReadActive(() => SettingsStore.VolumeActive);
        _updatingPreferences = true;
        try
        {
            _flyout.Brightness.Active.IsChecked = _brightnessActive;
            _flyout.Volume.Active.IsChecked = _volumeActive;
        }
        finally { _updatingPreferences = false; }
        await ApplyMonitoringAsync();
    }

    private static bool ReadActive(Func<bool> read)
    {
        try { return read(); }
        catch (Exception error) { SettingsStore.Log(error.ToString()); return true; }
    }

    private Task ApplyMonitoringAsync()
    {
        // Stop the unchecked feature immediately, even if the other feature is
        // still draining cancellation. Checkboxes remain usable throughout.
        if (!_brightnessActive) StopBrightness();
        if (!_volumeActive) StopVolume();
        _flyout?.SetControllers(_controller, _brightness);
        if (!_applyTask.IsCompleted) return _applyTask;
        return _applyTask = ReconcileMonitoringAsync();
    }

    private async Task ReconcileMonitoringAsync()
    {
        while (true)
        {
            var brightnessStop = _brightnessStopTask;
            var volumeStop = _volumeStopTask;
            await Task.WhenAll(brightnessStop, volumeStop);
            if (brightnessStop != _brightnessStopTask || volumeStop != _volumeStopTask) continue;

            // Read the latest choices after draining: a quick off/on/off must
            // neither recreate an unchecked controller nor replay its old input.
            if (_exiting || (!_brightnessActive && !_volumeActive))
            {
                _ddc?.Dispose(); _ddc = null;
                return;
            }
            _ddc ??= new DdcClient();
            if (_brightnessActive && _brightness is null) StartBrightness(_ddc);
            if (_volumeActive && _controller is null) StartVolume(_ddc);
            _flyout?.SetControllers(_controller, _brightness);
            return;
        }
    }

    private void StartBrightness(DdcClient ddc)
    {
        _brightness = new BrightnessController(ddc);
        _brightness.SetSuspended(_suspended);
        try
        {
            _brightnessMediaKeys = new BrightnessMediaKeys();
            _brightnessMediaKeys.SetSuspended(_suspended);
            _brightnessMediaKeys.Step += delta => _brightness?.Adjust(delta);
        }
        catch (Exception error)
        {
            SettingsStore.Log(error.ToString());
        }
        try
        {
            _brightnessHotkeys = new BrightnessHotkeys();
            _brightnessHotkeys.Step += delta => _brightness?.Adjust(delta);
        }
        catch (Exception error)
        {
            SettingsStore.Log(error.ToString());
        }
    }

    private void StartVolume(DdcClient ddc)
    {
        _controller = new SyncController(ddc);
        _controller.SetSuspended(_suspended);
        try { _volumeMediaKeys = new VolumeMediaKeys(_controller.TryQueueVolumeKey); }
        catch (Exception error) { SettingsStore.Log(error.ToString()); }
        _controller.Start(_volumeMediaKeys is not null);
    }

    private void UpdateStartupItem()
    {
        if (_flyout is null) return;
        try { SetChecked(_flyout.StartupItem, SettingsStore.StartsWithWindows); _flyout.StartupItem.IsEnabled = true; }
        catch (Exception error) { _flyout.StartupItem.IsEnabled = false; SettingsStore.Log(error.ToString()); }
    }

    private void SetChecked(System.Windows.Controls.MenuItem item, bool value)
    {
        _updatingPreferences = true;
        try { item.IsChecked = value; }
        finally { _updatingPreferences = false; }
    }

    private void StopVolume()
    {
        var controller = _controller;
        _controller = null;
        _volumeMediaKeys?.Dispose(); _volumeMediaKeys = null;
        if (controller is null) return;
        controller.SetSuspended(true);
        _volumeStopTask = controller.CloseAsync();
    }

    private void StopBrightness()
    {
        var brightness = _brightness;
        _brightness = null;
        _brightnessMediaKeys?.Dispose(); _brightnessMediaKeys = null;
        _brightnessHotkeys?.Dispose(); _brightnessHotkeys = null;
        if (brightness is null) return;
        brightness.SetSuspended(true);
        _brightnessStopTask = brightness.CloseAsync();
    }

    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        if (_exiting) return;
        _brightnessMediaKeys?.Reset();
        _brightness?.Invalidate();
        _controller?.TopologyChanged();
    });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume)
            Dispatcher.InvokeAsync(() =>
            {
                _suspended = e.Mode == PowerModes.Suspend;
                if (_exiting) return;
                _brightnessMediaKeys?.SetSuspended(_suspended);
                _brightness?.SetSuspended(_suspended);
                _controller?.SetSuspended(_suspended);
            });
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _brightnessActive = _volumeActive = false;
        if (_flyout is not null) _flyout.IsOpen = false;
        await ApplyMonitoringAsync();
        _flyout?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        _flyout?.Dispose();
        _brightnessMediaKeys?.Dispose();
        _volumeMediaKeys?.Dispose();
        _brightnessHotkeys?.Dispose();
        _brightness?.Dispose();
        _controller?.Dispose();
        _ddc?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
