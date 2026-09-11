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
    private Forms.ToolStripMenuItem? _statusItem, _levelsItem, _startupItem;
    private SyncController? _controller;
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

        _controller = new SyncController();
        _statusItem = new Forms.ToolStripMenuItem { Enabled = false };
        _levelsItem = new Forms.ToolStripMenuItem { Enabled = false };
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
        menu.Items.AddRange([_statusItem, _levelsItem, new Forms.ToolStripSeparator(),
            _startupItem, new Forms.ToolStripSeparator()]);
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
        menu.Opening += (_, _) => UpdateStartupItem();
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Monitor Sync", Visible = true, ContextMenuStrip = menu
        };
        _controller.Changed += (_, _) => UpdateStatus();
        UpdateStatus();
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        _controller.Start();
    }

    private void UpdateStartupItem()
    {
        if (_startupItem is null) return;
        try { _startupItem.Checked = SettingsStore.StartsWithWindows; _startupItem.Enabled = true; }
        catch (Exception error) { _startupItem.Enabled = false; SettingsStore.Log(error.ToString()); }
    }

    private void UpdateStatus()
    {
        if (_controller is null || _tray is null || _statusItem is null || _levelsItem is null) return;
        _statusItem.Text = _controller.Status;
        _levelsItem.Text = _controller.Levels;
        _levelsItem.Visible = _controller.Levels.Length > 0;
        var tooltip = "Monitor Sync — " + _controller.Status;
        _tray.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() => _controller?.TopologyChanged());
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume)
            Dispatcher.InvokeAsync(() => _controller?.SetSuspended(e.Mode == PowerModes.Suspend));
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        if (_controller is not null) await _controller.CloseAsync();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        _controller?.Dispose();
        var menu = _tray?.ContextMenuStrip;
        _tray?.Dispose();
        menu?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
