using System.ComponentModel;
using System.Runtime.InteropServices;
using MonitorSync.Core;

namespace MonitorSync.Windows;

/// <summary>Used only by the isolated worker. No capabilities-string probing.</summary>
public sealed class PhysicalMonitors : IDisposable
{
    private readonly Dictionary<string, PhysicalMonitor> _handles = new(StringComparer.OrdinalIgnoreCase);

    public MonitorDescriptor[] Enumerate()
    {
        Dispose();
        var result = new List<MonitorDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logical = new List<IntPtr>();
        MonitorEnumProc callback = (IntPtr handle, IntPtr dc, ref Rect rect, IntPtr data) =>
        { logical.Add(handle); return true; };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) ThrowLast("Enumerate displays");
        GC.KeepAlive(callback);
        foreach (var handle in logical)
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(handle, ref info)) continue;
            var device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(info.Device, 0, ref device, 1) || string.IsNullOrWhiteSpace(device.Id)) continue;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(handle, out var count) || count == 0) continue;
            if (count > 16) continue;
            var physical = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(handle, count, physical)) continue;
            // A logical display with several physical handles cannot be paired
            // reliably by its first display-device path. Do not guess in clone/MST cases.
            var unique = seen.Add(device.Id);
            if (count != 1 || !unique)
            {
                foreach (var monitor in physical) DestroyPhysicalMonitor(monitor.Handle);
                if (_handles.Remove(device.Id, out var previous)) DestroyPhysicalMonitor(previous.Handle);
                result.RemoveAll(item => string.Equals(item.Id, device.Id, StringComparison.OrdinalIgnoreCase));
                result.Add(new(device.Id, device.Name, null, null, "Ambiguous physical display mapping; use extended displays."));
                continue;
            }
            _handles.Add(device.Id, physical[0]);
            var errors = new List<string>();
            VolumeReading? ReadFeature(byte code)
            {
                try { return Read(device.Id, code); }
                catch (Exception e) when (e is IOException or Win32Exception) { errors.Add(e.Message); return null; }
            }
            var volume = ReadFeature(0x62);
            var brightness = ReadFeature(0x10);
            result.Add(new(device.Id, physical[0].Description, volume, brightness,
                errors.Count == 0 ? null : string.Join(" ", errors)));
        }
        return result.ToArray();
    }

    public VolumeReading Read(string id, byte code)
    {
        var handle = Get(id, code);
        if (!GetVCPFeatureAndVCPFeatureReply(handle, code, IntPtr.Zero, out var current, out var maximum))
            ThrowLast($"Read VCP 0x{code:X2}");
        var reading = new VolumeReading(current, maximum);
        reading.Validate();
        return reading;
    }

    public void Write(string id, byte code, uint value)
    {
        var handle = Get(id, code);
        // Verify the feature/range on this live handle before writing.
        var range = Read(id, code);
        if (value > range.Maximum) throw new IOException("Requested value exceeds the monitor's reported range.");
        if (!SetVCPFeature(handle, code, value)) ThrowLast($"Write VCP 0x{code:X2}");
    }

    private IntPtr Get(string id, byte code)
    {
        if (code is not (0x10 or 0x62)) throw new IOException("Only brightness and volume are supported.");
        if (!_handles.TryGetValue(id, out var physical))
            throw new IOException("The paired monitor is unavailable. Refresh displays.");
        return physical.Handle;
    }

    public void Dispose()
    {
        foreach (var monitor in _handles.Values) DestroyPhysicalMonitor(monitor.Handle);
        _handles.Clear();
    }

    private static void ThrowLast(string operation) => throw new Win32Exception(Marshal.GetLastWin32Error(), operation + " failed");
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice display, uint flags);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] physical);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, IntPtr type, out uint current, out uint maximum);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
    [DllImport("dxva2.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitor(IntPtr monitor);
}
