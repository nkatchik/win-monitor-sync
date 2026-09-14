using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

internal static class Program
{
    // This small renderer supports the paths, circles, rounded rectangles, clipping and
    // rotations used by our source SVG. Icon generation is an offline Windows step.
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Usage: GenerateIcons <assets-directory>");
        var directory = Path.GetFullPath(args[0]);
        var source = XDocument.Load(Path.Combine(directory, "monitor-sync.svg")).Root!;
        if (source.Attribute("viewBox")?.Value != "0 0 24 24")
            throw new NotSupportedException("Expected the icon's 24 by 24 viewBox.");
        var ink = (Color)ColorConverter.ConvertFromString(source.Attribute("color")?.Value ?? "black");
        WriteIcon(source, Path.Combine(directory, "monitor-sync.ico"), ink);
        // Black background / white glyph coverage lets the tray use any high-contrast palette.
        WriteIcon(source, Path.Combine(directory, "monitor-sync-tray.ico"), Colors.White, Brushes.Black);
    }

    private static void WriteIcon(XElement source, string path, Color ink, Brush? background = null)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
        var frames = sizes.Select(size => Render(source, size, ink, background)).ToArray();
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Length);
        var offset = 6 + sizes.Length * 16;
        for (var i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(frames[i].Length);
            writer.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var frame in frames) writer.Write(frame);
        Console.WriteLine(path);
    }

    private static byte[] Render(XElement source, int size, Color ink, Brush? background)
    {
        const int samples = 8;
        var renderSize = size * samples;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(renderSize / 24.0, renderSize / 24.0));
            Draw(dc, source, source, new SolidColorBrush(ink), background);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(renderSize, renderSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        // Average coverage at the final pixel size. Working in premultiplied alpha
        // keeps rounded corners clean instead of adding a dark or light fringe.
        var rendered = new byte[renderSize * renderSize * 4];
        bitmap.CopyPixels(rendered, renderSize * 4, 0);
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                for (var channel = 0; channel < 4; channel++)
                {
                    var sum = 0;
                    for (var sy = 0; sy < samples; sy++)
                        for (var sx = 0; sx < samples; sx++)
                            sum += rendered[((y * samples + sy) * renderSize + x * samples + sx) * 4 + channel];
                    pixels[(y * size + x) * 4 + channel] = (byte)((sum + samples * samples / 2) / (samples * samples));
                }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void Draw(DrawingContext dc, XElement element, XElement root, Brush ink, Brush? background)
    {
        var name = element.Name.LocalName;
        if (name is "defs" or "title" or "desc") return;
        var transform = element.Attribute("transform")?.Value;
        if (transform is not null)
        {
            if (!transform.StartsWith("rotate(", StringComparison.Ordinal) || !transform.EndsWith(')'))
                throw new NotSupportedException("Only rotation transforms are supported.");
            var values = transform[7..^1].Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            if (values.Length != 3) throw new NotSupportedException("Expected rotate(angle cx cy).");
            dc.PushTransform(new RotateTransform(values[0], values[1], values[2]));
        }
        var clipping = element.Attribute("clip-path")?.Value;
        if (clipping is not null)
        {
            if (!clipping.StartsWith("url(#", StringComparison.Ordinal) || !clipping.EndsWith(')'))
                throw new NotSupportedException("Only local clip paths are supported.");
            var id = clipping[5..^1];
            var clip = root.Descendants().Single(e => e.Attribute("id")?.Value == id);
            var path = clip.Elements().Single();
            if (path.Name.LocalName != "path") throw new NotSupportedException("Expected a path clip.");
            dc.PushClip(Geometry.Parse(path.Attribute("d")!.Value));
        }
        Geometry? geometry = name switch
        {
            "path" => Geometry.Parse(element.Attribute("d")!.Value),
            "circle" => new EllipseGeometry(new Point(Number(element, "cx"), Number(element, "cy")), Number(element, "r"), Number(element, "r")),
            "rect" => new RectangleGeometry(new Rect(Number(element, "x", 0), Number(element, "y", 0), Number(element, "width"), Number(element, "height")),
                Number(element, "rx", 0), Number(element, "ry", Number(element, "rx", 0))),
            "svg" or "g" => null,
            _ => throw new NotSupportedException($"Unsupported SVG element: {name}")
        };
        if (geometry is not null)
        {
            var fill = element.Attribute("id")?.Value == "background" && background is not null
                ? background : Paint(element, "fill", "black", ink);
            Pen? pen = null;
            if (Paint(element, "stroke", "none", ink) is { } stroke)
            {
                var round = Inherit(element, "stroke-linecap", "butt") == "round";
                pen = new Pen(stroke, double.Parse(Inherit(element, "stroke-width", "1"), CultureInfo.InvariantCulture))
                {
                    StartLineCap = round ? PenLineCap.Round : PenLineCap.Flat,
                    EndLineCap = round ? PenLineCap.Round : PenLineCap.Flat,
                    LineJoin = Inherit(element, "stroke-linejoin", "miter") == "round" ? PenLineJoin.Round : PenLineJoin.Miter
                };
            }
            dc.DrawGeometry(fill, pen, geometry);
        }
        else foreach (var child in element.Elements()) Draw(dc, child, root, ink, background);
        if (clipping is not null) dc.Pop();
        if (transform is not null) dc.Pop();
    }

    private static Brush? Paint(XElement element, string name, string fallback, Brush ink) =>
        Inherit(element, name, fallback) switch
        {
            "none" => null,
            "currentColor" => ink,
            var value => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value))
        };

    private static string Inherit(XElement element, string name, string fallback) => element.AncestorsAndSelf()
        .Select(e => e.Attribute(name)?.Value).FirstOrDefault(v => v is not null) ?? fallback;

    private static double Number(XElement element, string name, double? fallback = null) => element.Attribute(name) is { } attribute
        ? double.Parse(attribute.Value, CultureInfo.InvariantCulture)
        : fallback ?? throw new InvalidDataException($"Missing SVG attribute: {name}");
}
