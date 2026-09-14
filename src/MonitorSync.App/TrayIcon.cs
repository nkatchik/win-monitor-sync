using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
using Imaging = System.Windows.Media.Imaging;
using Media = System.Windows.Media;

namespace MonitorSync.App;

internal sealed class TrayIcon(Forms.NotifyIcon tray) : IDisposable
{
    private Icon? _icon;
    private (int Size, int Foreground, int Background)? _appearance;

    public void Refresh()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        var dpi = taskbar == IntPtr.Zero ? 96u : GetDpiForWindow(taskbar);
        var size = Math.Clamp(GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi == 0 ? 96u : dpi), 16, 256);
        var contrast = Forms.SystemInformation.HighContrast;
        var foreground = contrast ? SystemColors.WindowText : Color.Empty;
        var background = contrast ? SystemColors.Window : Color.Empty;
        var appearance = (size, foreground.ToArgb(), background.ToArgb());
        if (_appearance == appearance) return;

        var next = CreateIcon(size, foreground, background);
        try { tray.Icon = next; }
        catch { next.Dispose(); throw; }
        _icon?.Dispose();
        _icon = next;
        _appearance = appearance;
    }

    private static Icon CreateIcon(int size, Color foreground, Color background)
    {
        var contrast = !foreground.IsEmpty;
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream(contrast ? "MonitorSync.Contrast.ico" : "MonitorSync.Icon.ico")
            ?? throw new InvalidDataException("The tray icon resource is missing.");
        var decoder = new Imaging.IconBitmapDecoder(stream, Imaging.BitmapCreateOptions.PreservePixelFormat, Imaging.BitmapCacheOption.OnLoad);
        var frames = decoder.Frames.OrderBy(frame => frame.PixelWidth).ToArray();
        Imaging.BitmapSource source = frames.FirstOrDefault(frame => frame.PixelWidth >= size) ?? frames[^1];
        if (source.PixelWidth != size || source.PixelHeight != size)
            source = new Imaging.TransformedBitmap(source, new Media.ScaleTransform((double)size / source.PixelWidth, (double)size / source.PixelHeight));
        var pixels = new byte[size * size * 4];
        new Imaging.FormatConvertedBitmap(source, Media.PixelFormats.Bgra32, null, 0).CopyPixels(pixels, size * 4, 0);
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        // Preserve the artwork's colours and rounded-corner alpha. In high contrast,
        // the monochrome resource supplies glyph coverage for the system colour pair.
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var offset = (y * size + x) * 4;
                var coverage = pixels[offset + 2];
                int Mix(byte back, byte front) => (back * (255 - coverage) + front * coverage + 127) / 255;
                var color = contrast
                    ? Color.FromArgb(pixels[offset + 3], Mix(background.R, foreground.R), Mix(background.G, foreground.G), Mix(background.B, foreground.B))
                    : Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]);
                bitmap.SetPixel(x, y, color);
            }

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
