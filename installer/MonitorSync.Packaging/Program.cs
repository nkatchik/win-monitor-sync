using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

if (args is not [var source, var destination, var architecture] || architecture is not ("x64" or "arm64"))
{
    Console.Error.WriteLine("Usage: MonitorSync.Packaging <publish-directory> <output.wxs> <x64|arm64>");
    return 1;
}

source = Path.GetFullPath(source);
XNamespace ns = "http://wixtoolset.org/schemas/v4/wxs";
var root = new XElement(ns + "DirectoryRef", new XAttribute("Id", "INSTALLFOLDER"));
var components = new XElement(ns + "ComponentGroup", new XAttribute("Id", "Payload"));
var directories = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase) { [""] = root };
var installedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal);
var count = 0;

string Id(string path) => Convert.ToHexString(SHA256.HashData(
    Encoding.UTF8.GetBytes("MonitorSync.per-user.v1/" + architecture + "/" + path.ToLowerInvariant())))[..32];

string ComponentGuid(string hash)
{
    // RFC 9562 version 8: stable application-defined IDs, scoped to this product,
    // architecture and relative path. Mixed file/registry components need explicit GUIDs.
    var bytes = Convert.FromHexString(hash);
    bytes[6] = (byte)((bytes[6] & 0x0f) | 0x80);
    bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
    return new Guid(bytes, bigEndian: true).ToString("D");
}

XElement GetDirectory(string path)
{
    if (directories.TryGetValue(path, out var existing)) return existing;
    var parent = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
    var directory = new XElement(ns + "Directory", new XAttribute("Id", "d" + Id(path)),
        new XAttribute("Name", Path.GetFileName(path)));
    GetDirectory(parent).Add(directory);
    directories.Add(path, directory);
    return directory;
}

foreach (var file in files)
{
    var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
    if (!installedPaths.Add(relative)) throw new IOException("Duplicate Windows file path: " + relative);
    var hash = Id(relative);
    var componentId = "c" + hash;
    var directory = GetDirectory(Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "");
    var component = new XElement(ns + "Component", new XAttribute("Id", componentId),
        new XAttribute("Guid", ComponentGuid(hash)),
        new XElement(ns + "File", new XAttribute("Id", "f" + hash), new XAttribute("Source", file)),
        // Per-user components require an HKCU key path (MSI ICE38).
        new XElement(ns + "RegistryValue", new XAttribute("Root", "HKCU"),
            new XAttribute("Key", @"Software\MonitorSync\Components\" + architecture),
            new XAttribute("Name", hash), new XAttribute("Type", "integer"),
            new XAttribute("Value", "1"), new XAttribute("KeyPath", "yes")));
    directory.Add(component);
    components.Add(new XElement(ns + "ComponentRef", new XAttribute("Id", componentId)));
    count++;
}
if (count == 0) throw new IOException("The publish directory contains no files.");

// MSI also needs explicit removal of user-profile directories (ICE64).
foreach (var (path, directory) in directories)
{
    var hash = Id("directory/" + path);
    var componentId = "c" + hash;
    directory.Add(new XElement(ns + "Component", new XAttribute("Id", componentId), new XAttribute("Guid", ComponentGuid(hash)),
        new XElement(ns + "RemoveFolder", new XAttribute("Id", "r" + hash), new XAttribute("On", "uninstall")),
        new XElement(ns + "RegistryValue", new XAttribute("Root", "HKCU"),
            new XAttribute("Key", @"Software\MonitorSync\Components\" + architecture),
            new XAttribute("Name", hash), new XAttribute("Type", "integer"),
            new XAttribute("Value", "1"), new XAttribute("KeyPath", "yes"))));
    components.Add(new XElement(ns + "ComponentRef", new XAttribute("Id", componentId)));
}
new XDocument(new XElement(ns + "Wix", new XElement(ns + "Fragment", root, components))).Save(destination);
Console.WriteLine($"Generated per-user installer components for {count} files.");
return 0;
