using System.Runtime.InteropServices;

namespace MonitorSync.Windows;

public sealed record CursorDisplay(IntPtr Handle, string Id, int Left, int Top, int Right, int Bottom, double Scale)
{
    public static CursorDisplay? Capture()
    {
        if (!GetCursorPos(out var point)) return null;
        // MONITOR_DEFAULTTONULL: never substitute the nearest or primary display.
        var handle = MonitorFromPoint(point, 0);
        return FromHandle(handle);
    }

    public static CursorDisplay? FindById(string id)
    {
        CursorDisplay? found = null;
        var count = 0;
        MonitorEnumProc callback = (IntPtr handle, IntPtr dc, ref Rect rect, IntPtr data) =>
        {
            var display = FromHandle(handle);
            if (display is not null && string.Equals(display.Id, id, StringComparison.OrdinalIgnoreCase))
            { found = display; count++; }
            return true;
        };
        var success = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return success && count == 1 ? found : null;
    }

    private static CursorDisplay? FromHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(handle, ref info)) return null;
        var device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
        if (!EnumDisplayDevices(info.Device, 0, ref device, 1) || (device.Flags & 1) == 0 ||
            string.IsNullOrWhiteSpace(device.Id)) return null;
        for (uint index = 1; ; index++)
        {
            var other = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(info.Device, index, ref other, 1)) break;
            if ((other.Flags & 1) != 0) return null;
        }
        var scale = GetDpiForMonitor(handle, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        return new(handle, device.Id, info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom, scale);
    }

    public bool IsCurrent() => Capture() is { } current && current.Handle == Handle &&
        string.Equals(current.Id, Id, StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
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
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice display, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}
