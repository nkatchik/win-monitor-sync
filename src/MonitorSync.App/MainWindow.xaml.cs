using System.ComponentModel;
using System.Windows;

namespace MonitorSync.App;

public partial class MainWindow : Window
{
    private readonly SyncController _controller;
    public MainWindow(SyncController controller)
    {
        InitializeComponent();
        _controller = controller;
        DataContext = controller;
    }
    private async void EnableClick(object sender, RoutedEventArgs e) => await _controller.EnableAsync();
    private async void PauseClick(object sender, RoutedEventArgs e) => await _controller.PauseAsync();
    private async void RefreshClick(object sender, RoutedEventArgs e) => await _controller.RefreshAsync();
    private async void BrightnessClick(object sender, RoutedEventArgs e) => await _controller.ApplyBrightnessAsync();
    private async void ExitClick(object sender, RoutedEventArgs e) => await ((App)System.Windows.Application.Current).ExitAsync();
    private async void DiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        { FileName = "monitor-sync-diagnostics.json", DefaultExt = ".json", Filter = "JSON report|*.json" };
        if (dialog.ShowDialog(this) == true) await _controller.SaveDiagnosticsAsync(dialog.FileName);
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!((App)System.Windows.Application.Current).IsExiting) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
}
