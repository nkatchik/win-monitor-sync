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
    private SyncController? _controller;
    public bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            SettingsStore.Log(args.Exception.ToString());
            System.Windows.MessageBox.Show(args.Exception.Message, "Monitor Sync", MessageBoxButton.OK, MessageBoxImage.Error);
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
        if (!created)
        {
            System.Windows.MessageBox.Show("Monitor Sync is already running. Open it from the notification area.", "Monitor Sync");
            Shutdown(); return;
        }
        _controller = new SyncController();
        var window = new MainWindow(_controller);
        MainWindow = window;
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Monitor Sync", Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _tray.ContextMenuStrip.Items.Add("Open Monitor Sync", null, (_, _) => ShowWindow());
        _tray.ContextMenuStrip.Items.Add("Pause sync", null, async (_, _) => await _controller.PauseAsync());
        _tray.ContextMenuStrip.Items.Add("Exit", null, async (_, _) => await ExitAsync());
        _tray.DoubleClick += (_, _) => ShowWindow();
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        if (!e.Args.Contains("--background") || !SettingsStore.Load().SyncEnabled) window.Show();
        await _controller.InitializeAsync();
    }

    private void ShowWindow() { MainWindow.Show(); MainWindow.WindowState = WindowState.Normal; MainWindow.Activate(); }
    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(async () =>
    { if (_controller is not null) await _controller.TopologyChangedAsync(); });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume) DisplayChanged(sender, EventArgs.Empty);
    }
    public async Task ExitAsync()
    {
        if (IsExiting) return;
        IsExiting = true;
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        if (_controller is not null) await _controller.CloseAsync();
        _tray?.Dispose();
        Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose(); _instance?.Dispose(); base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Closing the settings window normally hides it; logout must close it.
        IsExiting = true;
        base.OnSessionEnding(e);
    }
}
