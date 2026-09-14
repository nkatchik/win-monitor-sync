using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

internal static class Program
{
    // This small renderer supports the monochrome paths, circles, clipping and
    // rotations used by our source SVG. Icon generation is an offline Windows step.
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Usage: GenerateIcons <assets-directory>");
        var directory = Path.GetFullPath(args[0]);
        var source = XDocument.Load(Path.Combine(directory, "monitor-sync.svg")).Root!;
        if (source.Attribute("viewBox")?.Value != "0 0 24 24")
            throw new NotSupportedException("Expected the icon's 24 by 24 viewBox.");
        WriteIcon(source, Path.Combine(directory, "monitor-sync-tray.ico"), Colors.Black);
        // A neutral application icon stays visible on both light and dark Explorer backgrounds.
        WriteIcon(source, Path.Combine(directory, "monitor-sync.ico"), Color.FromRgb(115, 115, 115));
    }

    private static void WriteIcon(XElement source, string path, Color ink)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
        var frames = sizes.Select(size => Render(source, size, ink)).ToArray();
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

    private static byte[] Render(XElement source, int size, Color ink)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 24.0, size / 24.0));
            Draw(dc, source, source, new SolidColorBrush(ink));
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void Draw(DrawingContext dc, XElement element, XElement root, Brush ink)
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
            "svg" or "g" => null,
            _ => throw new NotSupportedException($"Unsupported SVG element: {name}")
        };
        if (geometry is not null)
        {
            var fill = Paint(element, "fill", "black", ink);
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
        else foreach (var child in element.Elements()) Draw(dc, child, root, ink);
        if (clipping is not null) dc.Pop();
        if (transform is not null) dc.Pop();
    }

    private static Brush? Paint(XElement element, string name, string fallback, Brush ink) =>
        Inherit(element, name, fallback) switch
        {
            "none" => null,
            "currentColor" or "black" => ink,
            var value => throw new NotSupportedException($"Unsupported SVG paint: {value}")
        };

    private static string Inherit(XElement element, string name, string fallback) => element.AncestorsAndSelf()
        .Select(e => e.Attribute(name)?.Value).FirstOrDefault(v => v is not null) ?? fallback;

    private static double Number(XElement element, string name) => double.Parse(element.Attribute(name)!.Value, CultureInfo.InvariantCulture);
}
