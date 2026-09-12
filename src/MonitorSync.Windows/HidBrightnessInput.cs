using System.Runtime.InteropServices;

namespace MonitorSync.Windows;

public readonly record struct BrightnessKeyReport(byte Id, bool Up, bool Down);

/// <summary>Parses only the standard display-brightness buttons on the HID Consumer page.</summary>
public sealed class HidBrightnessInput : IDisposable
{
    private IntPtr _data;
    private readonly ushort[] _usages;
    private readonly ushort _reportLength;

    private HidBrightnessInput(IntPtr data, ushort length, uint usageCount)
    {
        _data = data;
        _reportLength = length;
        _usages = new ushort[usageCount];
    }

    public static HidBrightnessInput? Open(IntPtr device)
    {
        uint length = 0;
        if (GetRawInputDeviceInfo(device, 0x20000005, IntPtr.Zero, ref length) == uint.MaxValue ||
            length is 0 or > 65536) return null;
        var data = Marshal.AllocHGlobal((int)length);
        try
        {
            if (GetRawInputDeviceInfo(device, 0x20000005, data, ref length) == uint.MaxValue ||
                HidP_GetCaps(data, out var caps) < 0 || caps.Page != 0x0C || caps.Usage != 1 ||
                caps.ReportLength == 0) return null;
            var count = HidP_MaxUsageListLength(0, 0x0C, data);
            if (count is 0 or > 4096) return null;
            var input = new HidBrightnessInput(data, caps.ReportLength, count);
            data = IntPtr.Zero;
            return input;
        }
        finally { if (data != IntPtr.Zero) Marshal.FreeHGlobal(data); }
    }

    public BrightnessKeyReport? Read(byte[] report)
    {
        ObjectDisposedException.ThrowIf(_data == IntPtr.Zero, this);
        if (report.Length != _reportLength) return null;
        var count = (uint)_usages.Length;
        if (HidP_GetUsages(0, 0x0C, 0, _usages, ref count, _data, report, (uint)report.Length) < 0)
            return null;
        var up = false; var down = false;
        for (var i = 0; i < count; i++)
        {
            up |= _usages[i] == 0x6F;
            down |= _usages[i] == 0x70;
        }
        // Keyboard backlight (0x79/0x7A), volume, and ordinary keys are not display brightness.
        return new(report[0], up, down);
    }

    public void Dispose()
    {
        if (_data == IntPtr.Zero) return;
        Marshal.FreeHGlobal(_data);
        _data = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct HidCaps
    {
        [FieldOffset(0)] public ushort Usage;
        [FieldOffset(2)] public ushort Page;
        [FieldOffset(4)] public ushort ReportLength;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint length);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
    [DllImport("hid.dll")] private static extern uint HidP_MaxUsageListLength(int type, ushort page, IntPtr data);
    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int type, ushort page, ushort collection, [Out] ushort[] usages,
        ref uint count, IntPtr data, byte[] report, uint length);
}
