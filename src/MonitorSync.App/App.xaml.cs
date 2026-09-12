using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace MonitorSync.App;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _activeItem, _startupItem;
    private TraySliders? _sliders;
    private SyncController? _controller;
    private DdcClient? _ddc;
    private BrightnessController? _brightness;
    private BrightnessHotkeys? _brightnessHotkeys;
    private BrightnessMediaKeys? _brightnessMediaKeys;
    private VolumeMediaKeys? _volumeMediaKeys;
    private bool _active, _suspended, _exiting;
    private Task _stopTask = Task.CompletedTask;

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

        Forms.Application.EnableVisualStyles();
        _activeItem = new Forms.ToolStripMenuItem("Active");
        _activeItem.Click += async (_, _) =>
        {
            _activeItem.Enabled = false;
            try
            {
                await SetActiveAsync(!_active);
                SettingsStore.Active = _active;
            }
            catch (Exception error) { SettingsStore.Log(error.ToString()); }
            finally { if (!_exiting) _activeItem.Enabled = true; }
        };
        _startupItem = new Forms.ToolStripMenuItem("Start with Windows");
        try { SettingsStore.InitializeStartup(); }
        catch (Exception error) { SettingsStore.Log(error.ToString()); }
        UpdateStartupItem();
        _startupItem.Click += (_, _) =>
        {
            try { SettingsStore.StartsWithWindows = !SettingsStore.StartsWithWindows; }
            catch (Exception error) { SettingsStore.Log(error.ToString()); }
            UpdateStartupItem();
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([new Forms.ToolStripSeparator(), _activeItem,
            _startupItem, new Forms.ToolStripSeparator()]);
        _sliders = new TraySliders(menu);
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
        menu.Opening += (_, _) => UpdateStartupItem();
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Monitor Sync", Visible = true, ContextMenuStrip = menu
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button != Forms.MouseButtons.Left) return;
            menu.Show(Forms.Cursor.Position);
            // Give the popup focus so keyboard navigation and outside-click dismissal work.
            SetForegroundWindow(menu.Handle);
        };
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        var active = true;
        try { active = SettingsStore.Active; }
        catch (Exception error) { SettingsStore.Log(error.ToString()); }
        await SetActiveAsync(active);
    }

    private Task SetActiveAsync(bool active)
    {
        if (_exiting || _active == active) return Task.CompletedTask;
        _active = active;
        if (_activeItem is not null) _activeItem.Checked = active;
        if (!active) return StopMonitoringAsync();

        _ddc = new DdcClient();
        _controller = new SyncController(_ddc);
        _brightness = new BrightnessController(_ddc);
        _controller.SetSuspended(_suspended);
        _brightness.SetSuspended(_suspended);
        try { _volumeMediaKeys = new VolumeMediaKeys(_controller.TryQueueVolumeKey); }
        catch (Exception error) { SettingsStore.Log(error.ToString()); }
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
        _sliders?.SetControllers(_controller, _brightness);
        _controller.Start(_volumeMediaKeys is not null);
        return Task.CompletedTask;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    private void UpdateStartupItem()
    {
        if (_startupItem is null) return;
        try { _startupItem.Checked = SettingsStore.StartsWithWindows; _startupItem.Enabled = true; }
        catch (Exception error) { _startupItem.Enabled = false; SettingsStore.Log(error.ToString()); }
    }

    private Task StopMonitoringAsync()
    {
        if (!_stopTask.IsCompleted) return _stopTask;
        _sliders?.SetControllers(null, null);
        _volumeMediaKeys?.Dispose(); _volumeMediaKeys = null;
        _brightnessMediaKeys?.Dispose(); _brightnessMediaKeys = null;
        _brightnessHotkeys?.Dispose(); _brightnessHotkeys = null;
        var brightness = _brightness;
        var controller = _controller;
        var ddc = _ddc;
        _brightness = null;
        _controller = null;
        _ddc = null;
        // Reject queued input immediately, then drain cancellation before releasing the worker.
        brightness?.SetSuspended(true);
        controller?.SetSuspended(true);
        return _stopTask = CloseMonitoringAsync(brightness, controller, ddc);
    }

    private static async Task CloseMonitoringAsync(BrightnessController? brightness, SyncController? controller, DdcClient? ddc)
    {
        try
        {
            await Task.WhenAll(brightness?.CloseAsync() ?? Task.CompletedTask,
                controller?.CloseAsync() ?? Task.CompletedTask);
        }
        finally { ddc?.Dispose(); }
    }

    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        if (!_active || _exiting) return;
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
                if (!_active || _exiting) return;
                _brightnessMediaKeys?.SetSuspended(_suspended);
                _brightness?.SetSuspended(_suspended);
                _controller?.SetSuspended(_suspended);
            });
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _active = false;
        await StopMonitoringAsync();
        _sliders?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        _sliders?.Dispose();
        _brightnessMediaKeys?.Dispose();
        _volumeMediaKeys?.Dispose();
        _brightnessHotkeys?.Dispose();
        _brightness?.Dispose();
        _controller?.Dispose();
        _ddc?.Dispose();
        var menu = _tray?.ContextMenuStrip;
        _tray?.Dispose();
        menu?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
