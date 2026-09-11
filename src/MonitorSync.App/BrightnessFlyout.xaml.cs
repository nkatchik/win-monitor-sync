using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using MonitorSync.Windows;
using Media = System.Windows.Media;
using SystemColors = System.Windows.SystemColors;

namespace MonitorSync.App;

public partial class BrightnessFlyout : Window
{
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromMilliseconds(1800) };
    private readonly DispatcherTimer _cursor = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private CursorDisplay? _target;
    private int _presentation;
    private readonly Media.Effects.Effect? _shadow;

    public BrightnessFlyout()
    {
        InitializeComponent();
        _shadow = Surface.Effect;
        _hide.Tick += (_, _) => FadeOut();
        _cursor.Tick += (_, _) => { if (_target?.IsCurrent() != true) HideImmediately(); };
        Closed += (_, _) => { _hide.Stop(); _cursor.Stop(); };
    }

    public void ShowLevel(CursorDisplay target, int percent, bool pending)
    {
        percent = Math.Clamp(percent, 0, 100);
        Level.Visibility = Visibility.Visible;
        Message.Visibility = Visibility.Collapsed;
        Value.Text = percent.ToString(System.Globalization.CultureInfo.CurrentCulture);
        Fill.Width = 176 * percent / 100.0;
        ThumbPosition.X = Fill.Width - 6;
        Level.Opacity = pending ? 0.65 : 1;
        AutomationProperties.SetName(Level, $"Brightness {percent} percent{(pending ? ", adjusting" : "")}");
        Present(target);
    }

    public void ShowMessage(CursorDisplay target, string message)
    {
        Level.Visibility = Visibility.Collapsed;
        Message.Visibility = Visibility.Visible;
        Message.Text = message;
        Present(target);
    }

    private void Present(CursorDisplay target)
    {
        if (!target.IsCurrent()) return;
        _target = target;
        _presentation++;
        ApplyTheme();
        var wasVisible = IsVisible;
        Surface.BeginAnimation(OpacityProperty, null);
        Surface.Opacity = 1;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var width = (int)Math.Round(Width * target.Scale);
        var height = (int)Math.Round(Height * target.Scale);
        var x = Math.Max(target.Left, target.Left + (target.Right - target.Left - width) / 2);
        var y = Math.Max(target.Top, target.Bottom - height - (int)(48 * target.Scale));
        // Physical coordinates avoid mixing the DPI scales of adjacent screens.
        SetWindowPos(handle, new IntPtr(-1), x, y, width, height, 0x0010);
        if (!wasVisible)
        {
            Show();
            if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
                Surface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100)));
        }
        _hide.Stop(); _hide.Start();
        _cursor.Start();
    }

    private void ApplyTheme()
    {
        if (SystemParameters.HighContrast)
        {
            Surface.Background = SystemColors.WindowBrush;
            Surface.BorderBrush = SystemColors.WindowTextBrush;
            Foreground = SystemColors.WindowTextBrush;
            Track.Background = SystemColors.GrayTextBrush;
            Fill.Background = Thumb.Fill = SystemColors.HighlightBrush;
            Surface.Effect = null;
            return;
        }
        var light = true;
        Surface.Effect = _shadow;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("SystemUsesLightTheme") is not int value || value != 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }
        static Media.Brush Brush(string color) => new Media.SolidColorBrush((Media.Color)Media.ColorConverter.ConvertFromString(color));
        Surface.Background = Brush(light ? "#F3F3F3" : "#2C2C2C");
        Surface.BorderBrush = Brush(light ? "#D7D7D7" : "#494949");
        Foreground = Brush(light ? "#1A1A1A" : "#F5F5F5");
        Track.Background = Brush(light ? "#989898" : "#9A9A9A");
        Fill.Background = Thumb.Fill = Brush(light ? "#0067C0" : "#60CDFF");
    }

    private void FadeOut()
    {
        _hide.Stop();
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast) { HideImmediately(); return; }
        var generation = _presentation;
        var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        animation.Completed += (_, _) => { if (generation == _presentation) HideImmediately(); };
        Surface.BeginAnimation(OpacityProperty, animation);
    }

    public void HideImmediately()
    {
        _presentation++;
        _hide.Stop(); _cursor.Stop();
        Surface.BeginAnimation(OpacityProperty, null);
        Hide();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        // Tool window, click-through, and no activation (including mouse hover).
        SetWindowLongPtr(handle, -20, new IntPtr(style | 0x80 | 0x20 | 0x08000000));
        HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message == 0x0021) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
            return IntPtr.Zero;
        });
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
