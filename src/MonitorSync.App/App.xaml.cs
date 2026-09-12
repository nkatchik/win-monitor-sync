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
    private Forms.ToolStripMenuItem? _statusItem, _startupItem;
    private TraySliders? _sliders;
    private SyncController? _controller;
    private DdcClient? _ddc;
    private BrightnessController? _brightness;
    private BrightnessHotkeys? _brightnessHotkeys;
    private BrightnessMediaKeys? _brightnessMediaKeys;
    private VolumeMediaKeys? _volumeMediaKeys;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            SettingsStore.Log(args.Exception.ToString());
            if (_statusItem is not null) _statusItem.Text = "Monitor Sync encountered an error";
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
        _ddc = new DdcClient();
        _controller = new SyncController(_ddc);
        _brightness = new BrightnessController(_ddc);
        _statusItem = new Forms.ToolStripMenuItem { Enabled = false };
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
        menu.Items.AddRange([_statusItem, new Forms.ToolStripSeparator(),
            _startupItem, new Forms.ToolStripSeparator()]);
        _sliders = new TraySliders(menu, _controller, _brightness);
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
        menu.Opening += (_, _) => UpdateStartupItem();
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Monitor Sync", Visible = true, ContextMenuStrip = menu
        };
        _controller.Changed += (_, _) => UpdateStatus();
        try { _volumeMediaKeys = new VolumeMediaKeys(_controller.TryQueueVolumeKey); }
        catch (Exception error) { SettingsStore.Log(error.ToString()); }
        try
        {
            _brightnessMediaKeys = new BrightnessMediaKeys();
            _brightnessMediaKeys.Step += delta => _brightness.Adjust(delta);
        }
        catch (Exception error)
        {
            SettingsStore.Log(error.ToString());
        }
        try
        {
            _brightnessHotkeys = new BrightnessHotkeys();
            _brightnessHotkeys.Step += delta => _brightness.Adjust(delta);
        }
        catch (Exception error)
        {
            SettingsStore.Log(error.ToString());
        }
        UpdateStatus();
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        _controller.Start(_volumeMediaKeys is not null);
    }

    private void UpdateStartupItem()
    {
        if (_startupItem is null) return;
        try { _startupItem.Checked = SettingsStore.StartsWithWindows; _startupItem.Enabled = true; }
        catch (Exception error) { _startupItem.Enabled = false; SettingsStore.Log(error.ToString()); }
    }

    private void UpdateStatus()
    {
        if (_controller is null || _tray is null || _statusItem is null) return;
        _statusItem.Text = _controller.Status;
        var tooltip = "Monitor Sync — " + _controller.Status;
        _tray.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        _brightnessMediaKeys?.Reset();
        _brightness?.Invalidate();
        _controller?.TopologyChanged();
    });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume)
            Dispatcher.InvokeAsync(() =>
            {
                _brightnessMediaKeys?.SetSuspended(e.Mode == PowerModes.Suspend);
                _brightness?.SetSuspended(e.Mode == PowerModes.Suspend);
                _controller?.SetSuspended(e.Mode == PowerModes.Suspend);
            });
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _sliders?.Dispose();
        _brightnessMediaKeys?.Dispose();
        _volumeMediaKeys?.Dispose();
        _brightnessHotkeys?.Dispose();
        await Task.WhenAll(_brightness?.CloseAsync() ?? Task.CompletedTask,
            _controller?.CloseAsync() ?? Task.CompletedTask);
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
