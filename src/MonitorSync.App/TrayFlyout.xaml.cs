using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MonitorSync.App;

public partial class TrayFlyout : ContextMenu, IDisposable
{
    public TraySlider Volume { get; } = new("Volume", 2);
    public TraySlider Brightness { get; } = new("Brightness", 5);
    private SyncController? _volume;
    private BrightnessController? _brightness;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _probing, _disposed;
    private long _probeDue;

    public TrayFlyout()
    {
        InitializeComponent();
        // A standalone ContextMenu has no owning Window to invalidate its resources
        // when Application.ThemeMode changes. Share the live dictionary with the menu.
        Resources.MergedDictionaries.Add(Application.Current.Resources);
        VolumeItem.Header = Volume;
        BrightnessItem.Header = Brightness;
        Volume.ValueRequested += percent => { _volume?.SetVolumePercent(percent); Update(); };
        Brightness.ValueRequested += percent => { _brightness?.SetPercent(percent); Update(); };
        _refresh.Tick += Tick;
        Opened += (_, _) =>
        {
            // A tray-only application has no foreground window to own menu input.
            // Activate the menu's popup so keyboard input and outside-click dismissal work.
            if (PresentationSource.FromVisual(this) is HwndSource source)
                SetForegroundWindow(source.Handle);
            Brightness.Active.Focus();
            RefreshVisible();
        };
        Closed += (_, _) => _refresh.Stop();
    }

    public void SetControllers(SyncController? volume, BrightnessController? brightness)
    {
        if (_disposed) return;
        _refresh.Stop();
        if (_volume is not null) _volume.Changed -= Changed;
        if (_brightness is not null) _brightness.Changed -= Changed;
        _volume = volume;
        _brightness = brightness;
        if (_volume is not null) _volume.Changed += Changed;
        if (_brightness is not null) _brightness.Changed += Changed;
        Update();
        if (IsOpen) RefreshVisible();
    }

    public void ShowAtCursor()
    {
        if (_disposed) return;
        IsOpen = true;
    }

    private void RefreshVisible()
    {
        _probeDue = 0;
        Update();
        if (_brightness is null && _volume is null) return;
        _refresh.Start();
        Tick(this, EventArgs.Empty);
    }

    private void Changed(object? sender, EventArgs e) { if (IsOpen) Update(); }

    private async void Tick(object? sender, EventArgs e)
    {
        if (_disposed || !IsOpen) return;
        Update();
        if (_brightness is not { } brightness) return;
        if (_probing || _clock.ElapsedMilliseconds < _probeDue) return;
        _probing = true;
        try { await brightness.RefreshAsync(); }
        finally
        {
            _probing = false;
            _probeDue = _clock.ElapsedMilliseconds + 2000;
            if (!_disposed && IsOpen) Update();
        }
    }

    private void Update()
    {
        if (_disposed) return;
        var volume = _volume?.VolumePercent;
        var brightness = _brightness?.Percent;
        Volume.UpdateLevel(volume, _volume?.IsPending ?? false);
        Brightness.UpdateLevel(brightness, _brightness?.IsPending ?? false);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.System && e.SystemKey == Key.F4)
        {
            e.Handled = true;
            IsOpen = false;
        }
        base.OnPreviewKeyDown(e);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refresh.Stop();
        _refresh.Tick -= Tick;
        if (_volume is not null) _volume.Changed -= Changed;
        if (_brightness is not null) _brightness.Changed -= Changed;
        IsOpen = false;
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
