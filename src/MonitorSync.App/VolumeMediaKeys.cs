using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MonitorSync.App;

/// <summary>Intercepts the dedicated Windows volume keys only while a monitor route is controlled.</summary>
public sealed class VolumeMediaKeys : IDisposable
{
    private readonly HookProc _callback;
    private readonly HashSet<uint> _pressed = [];
    private IntPtr _hook;

    public VolumeMediaKeys(Func<int, bool> tryQueue)
    {
        _callback = (code, message, data) =>
        {
            if (code >= 0)
            {
                var key = unchecked((uint)Marshal.ReadInt32(data));
                if (key is >= 0xAD and <= 0xAF)
                {
                    var down = message.ToInt64() is 0x0100 or 0x0104;
                    var up = message.ToInt64() is 0x0101 or 0x0105;
                    if (down && key == 0xAD && _pressed.Contains(key)) return new IntPtr(1);
                    // The callback only queues intent on the dispatcher. No COM/DDC, waits, or UI here.
                    if (down && tryQueue(key == 0xAD ? 0 : key == 0xAF ? 2 : -2))
                    { _pressed.Add(key); return new IntPtr(1); }
                    if (up && _pressed.Remove(key)) return new IntPtr(1);
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        };
        _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not listen for volume keys.");
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _pressed.Clear();
        GC.KeepAlive(_callback);
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
