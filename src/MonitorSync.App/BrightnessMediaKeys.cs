using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using MonitorSync.Core;
using MonitorSync.Windows;

public sealed class BrightnessMediaKeys : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<IntPtr, HidBrightnessInput?> _devices = [];
    private readonly BrightnessKeyRepeater _keys;
    private readonly DispatcherTimer _repeat = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _suspended, _disposed;
    public event Action<int>? Step;

    public BrightnessMediaKeys()
    {
        var clock = Stopwatch.StartNew();
        _keys = new(() => clock.ElapsedMilliseconds);
        _source = new HwndSource(new HwndSourceParameters("Monitor Sync brightness media keys")
        { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
        _source.AddHook(HandleMessage);
        // INPUTSINK | DEVNOTIFY: receive Consumer Control reports while other apps have focus.
        if (!RegisterRawInputDevices([new() { Page = 0x0C, Usage = 1, Flags = 0x2100, Target = _source.Handle }],
            1, (uint)Marshal.SizeOf<RawDevice>()))
        {
            var error = Marshal.GetLastWin32Error();
            _source.Dispose();
            throw new Win32Exception(error, "Could not listen for brightness media keys.");
        }
        _repeat.Tick += (_, _) =>
        {
            if (_suspended || CursorDisplay.Capture() is null) { Reset(); return; }
            var step = _keys.Tick();
            if (step != 0) Step?.Invoke(step);
        };
    }

    private IntPtr HandleMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if (message == 0x00FE) // WM_INPUT_DEVICE_CHANGE: invalidate even if a handle was reused.
        {
            if (_devices.Remove(lParam, out var input)) input?.Dispose();
            _keys.Remove(lParam.ToInt64());
            if (!_keys.HasKeys) _repeat.Stop();
        }
        if (message == 0x00FF && !_suspended) ReadInput(lParam);
        // Let WPF/DefWindowProc perform WM_INPUT cleanup. Raw Input observes HID reports;
        // it does not suppress any native/vendor brightness handling.
        return IntPtr.Zero;
    }

    private void ReadInput(IntPtr raw)
    {
        var headerSize = (uint)Marshal.SizeOf<RawHeader>();
        uint length = 0;
        if (GetRawInputData(raw, 0x10000003, IntPtr.Zero, ref length, headerSize) == uint.MaxValue ||
            length < headerSize + 8 || length > 65536) return;
        var data = Marshal.AllocHGlobal((int)length);
        try
        {
            if (GetRawInputData(raw, 0x10000003, data, ref length, headerSize) != length) return;
            var header = Marshal.PtrToStructure<RawHeader>(data);
            if (header.Type != 2) return;
            var size = (uint)Marshal.ReadInt32(data, (int)headerSize);
            var count = (uint)Marshal.ReadInt32(data, (int)headerSize + 4);
            if (size == 0 || (ulong)size * count > length - headerSize - 8) return;
            if (!_devices.TryGetValue(header.Device, out var input))
                _devices[header.Device] = input = HidBrightnessInput.Open(header.Device);
            if (input is null) return;
            var report = new byte[size];
            for (var index = 0; index < count; index++)
            {
                Marshal.Copy(IntPtr.Add(data, checked((int)(headerSize + 8 + index * size))), report, 0, report.Length);
                if (input.Read(report) is not { } buttons) continue;
                var step = _keys.Update(header.Device.ToInt64(), buttons.Id, buttons.Up, buttons.Down);
                if (_keys.HasKeys) _repeat.Start(); else _repeat.Stop();
                if (step != 0) Step?.Invoke(step);
            }
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    public void Reset() { _keys.Clear(); _repeat.Stop(); }
    public void SetSuspended(bool suspended) { _suspended = suspended; Reset(); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _repeat.Stop(); _keys.Clear();
        RegisterRawInputDevices([new() { Page = 0x0C, Usage = 1, Flags = 1 }], 1, (uint)Marshal.SizeOf<RawDevice>());
        foreach (var input in _devices.Values) input?.Dispose();
        _devices.Clear();
        _source.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawDevice { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawHeader { public uint Type, Size; public IntPtr Device, Param; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint length, uint headerSize);
}
