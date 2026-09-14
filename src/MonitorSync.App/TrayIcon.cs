using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Imaging = System.Windows.Media.Imaging;
using Media = System.Windows.Media;

namespace MonitorSync.App;

internal sealed class TrayIcon(Forms.NotifyIcon tray) : IDisposable
{
    private Icon? _icon;
    private (int Size, int Color)? _appearance;

    public void Refresh()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        var dpi = taskbar == IntPtr.Zero ? 96u : GetDpiForWindow(taskbar);
        var size = Math.Clamp(GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi == 0 ? 96u : dpi), 16, 256);
        var color = GetColor();
        if (_appearance == (size, color.ToArgb())) return;

        var next = CreateIcon(size, color);
        try { tray.Icon = next; }
        catch { next.Dispose(); throw; }
        _icon?.Dispose();
        _icon = next;
        _appearance = (size, color.ToArgb());
    }

    private static Color GetColor()
    {
        if (Forms.SystemInformation.HighContrast) return SystemColors.WindowText;
        var light = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("SystemUsesLightTheme") is not int value || value != 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            SettingsStore.Log(error.ToString());
        }
        return light ? Color.FromArgb(32, 32, 32) : Color.FromArgb(246, 246, 247);
    }

    private static Icon CreateIcon(int size, Color color)
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("MonitorSync.Tray.ico")
            ?? throw new InvalidDataException("The tray icon resource is missing.");
        var decoder = new Imaging.IconBitmapDecoder(stream, Imaging.BitmapCreateOptions.PreservePixelFormat, Imaging.BitmapCacheOption.OnLoad);
        var frames = decoder.Frames.OrderBy(frame => frame.PixelWidth).ToArray();
        Imaging.BitmapSource source = frames.FirstOrDefault(frame => frame.PixelWidth >= size) ?? frames[^1];
        if (source.PixelWidth != size || source.PixelHeight != size)
            source = new Imaging.TransformedBitmap(source, new Media.ScaleTransform((double)size / source.PixelWidth, (double)size / source.PixelHeight));
        var pixels = new byte[size * size * 4];
        new Imaging.FormatConvertedBitmap(source, Media.PixelFormats.Bgra32, null, 0).CopyPixels(pixels, size * 4, 0);
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        // Keep the SVG's antialiased alpha at the selected DPI; only the ink changes.
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                bitmap.SetPixel(x, y, Color.FromArgb(pixels[(y * size + x) * 4 + 3], color));

        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
        _appearance = null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
