using System.Runtime.InteropServices;
using MonitorSync.Core;

namespace MonitorSync.Windows;

public readonly record struct BrightnessKeyReport(byte Id, bool Up, bool Down, bool Pulse = false);
public sealed record BrightnessInputInfo(uint VendorId, uint ProductId, ushort Page, ushort Usage, string[] Controls);

/// <summary>Uses the device's HID descriptor, never fixed report offsets or ordinary function keys.</summary>
public sealed class HidBrightnessInput : IDisposable
{
    private IntPtr _data;
    private readonly ushort[] _usages = new ushort[4096];
    private readonly ushort _reportLength;
    private readonly Control[] _controls;
    public BrightnessInputInfo Info { get; }

    private sealed record Control(ushort Page, ushort Usage, ushort Collection, byte Report,
        int Direction, bool Value, bool Relative, uint Maximum);

    private HidBrightnessInput(IntPtr data, HidCaps caps, DeviceInfo device, Control[] controls)
    {
        _data = data;
        _reportLength = caps.ReportLength;
        _controls = controls;
        Info = new(device.Vendor, device.Product, caps.Page, caps.Usage,
            controls.Select(c => $"{c.Page:X4}:{c.Usage:X4} report {c.Report} ({(c.Value ? "value" : "button")})").ToArray());
    }

    public static HidBrightnessInput? Open(IntPtr device)
    {
        var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
        var infoSize = info.Size;
        if (GetDeviceInfo(device, 0x2000000B, ref info, ref infoSize) == uint.MaxValue || info.Type != 2) return null;
        uint length = 0;
        if (GetRawInputDeviceInfo(device, 0x20000005, IntPtr.Zero, ref length) == uint.MaxValue ||
            length is 0 or > 65536) return null;
        var data = Marshal.AllocHGlobal((int)length);
        try
        {
            if (GetRawInputDeviceInfo(device, 0x20000005, data, ref length) == uint.MaxValue ||
                HidP_GetCaps(data, out var caps) < 0 || caps.ReportLength == 0 ||
                caps.Buttons > 4096 || caps.Values > 4096) return null;
            var controls = new List<Control>();
            void ReadCaps(bool values, ushort count)
            {
                if (count == 0) return;
                var buffer = Marshal.AllocHGlobal(count * 72);
                try
                {
                    var status = values ? HidP_GetValueCaps(0, buffer, ref count, data) : HidP_GetButtonCaps(0, buffer, ref count, data);
                    if (status < 0) return;
                    for (var index = 0; index < count; index++)
                    {
                        var cap = Marshal.PtrToStructure<ControlCaps>(IntPtr.Add(buffer, index * 72));
                        // Only scalar value fields: never infer a key from a value array or null encoding.
                        if (values && (cap.ReportCount == 0 || (cap.IsRange == 0 && cap.ReportCount != 1) ||
                            cap.BitSize is 0 or > 32 || cap.LogicalMin != 0 || cap.LogicalMax < 1)) continue;
                        var maximum = cap.IsRange != 0 ? cap.Maximum : cap.Minimum;
                        foreach (var usage in new ushort[] { 0x6F, 0x70, 0x20, 0x21, 4, 5 })
                        {
                            if (usage < cap.Minimum || usage > maximum) continue;
                            var direction = BrightnessUsages.Direction(cap.Page, usage, info.Vendor);
                            if (direction != 0) controls.Add(new(cap.Page, usage, cap.Collection, cap.Report,
                                direction, values, values && cap.Absolute == 0, values ? (uint)cap.LogicalMax : 1));
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            ReadCaps(false, caps.Buttons);
            ReadCaps(true, caps.Values);
            if (controls.Count == 0) return null;
            var input = new HidBrightnessInput(data, caps, info, controls.Distinct().ToArray());
            data = IntPtr.Zero;
            return input;
        }
        finally { if (data != IntPtr.Zero) Marshal.FreeHGlobal(data); }
    }

    public BrightnessKeyReport? Read(byte[] report)
    {
        ObjectDisposedException.ThrowIf(_data == IntPtr.Zero, this);
        if (report.Length != _reportLength) return null;
        var up = false; var down = false; var found = false; var pulse = false; var holding = false;
        foreach (var control in _controls)
        {
            if (control.Report != report[0]) continue;
            bool pressed;
            if (control.Value)
            {
                if (HidP_GetUsageValue(0, control.Page, control.Collection, control.Usage, out var value,
                    _data, report, (uint)report.Length) < 0) return null;
                pressed = value != 0 && value <= control.Maximum;
            }
            else
            {
                var count = (uint)_usages.Length;
                if (HidP_GetUsages(0, control.Page, control.Collection, _usages, ref count, _data,
                    report, (uint)report.Length) < 0 || count > _usages.Length) return null;
                pressed = _usages.AsSpan(0, (int)count).Contains(control.Usage);
            }
            found = true;
            up |= pressed && control.Direction > 0;
            down |= pressed && control.Direction < 0;
            pulse |= pressed && control.Relative;
            holding |= pressed && !control.Relative;
        }
        return found ? new(report[0], up, down, pulse && !holding) : null;
    }

    public static BrightnessInputInfo[] DescribeDevices()
    {
        uint count = 0;
        var size = (uint)Marshal.SizeOf<RawDevice>();
        if (GetRawInputDeviceList(null, ref count, size) == uint.MaxValue || count > 4096) return [];
        var devices = new RawDevice[count];
        var received = GetRawInputDeviceList(devices, ref count, size);
        if (received == uint.MaxValue) return [];
        var results = new List<BrightnessInputInfo>();
        foreach (var device in devices.Take((int)received).Where(d => d.Type == 2))
        {
            using var input = Open(device.Handle);
            if (input is not null) results.Add(input.Info);
        }
        return results.ToArray();
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
        [FieldOffset(46)] public ushort Buttons;
        [FieldOffset(48)] public ushort Values;
    }
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct ControlCaps
    {
        [FieldOffset(0)] public ushort Page;
        [FieldOffset(2)] public byte Report;
        [FieldOffset(6)] public ushort Collection;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(15)] public byte Absolute;
        [FieldOffset(18)] public ushort BitSize;
        [FieldOffset(20)] public ushort ReportCount;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(56)] public ushort Minimum;
        [FieldOffset(58)] public ushort Maximum;
    }
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct DeviceInfo
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Type;
        [FieldOffset(8)] public uint Vendor;
        [FieldOffset(12)] public uint Product;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RawDevice { public IntPtr Handle; public uint Type; }
    [DllImport("user32.dll")] private static extern uint GetRawInputDeviceList([Out] RawDevice[]? devices, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint length);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
    private static extern uint GetDeviceInfo(IntPtr device, uint command, ref DeviceInfo info, ref uint length);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
    [DllImport("hid.dll")] private static extern int HidP_GetButtonCaps(int type, IntPtr caps, ref ushort count, IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int type, IntPtr caps, ref ushort count, IntPtr data);
    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int type, ushort page, ushort collection, [Out] ushort[] usages,
        ref uint count, IntPtr data, byte[] report, uint length);
    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int type, ushort page, ushort collection, ushort usage,
        out uint value, IntPtr data, byte[] report, uint length);
}
