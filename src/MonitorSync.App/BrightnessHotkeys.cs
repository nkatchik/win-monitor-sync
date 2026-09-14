using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MonitorSync.App;

public sealed class BrightnessHotkeys : IDisposable
{
    private readonly HwndSource _source;
    public event Action<int>? Step;

    public BrightnessHotkeys()
    {
        _source = new HwndSource(new HwndSourceParameters("MonitorSync brightness shortcuts")
        { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
        _source.AddHook(HandleMessage);
        // Ctrl+Alt+Page Up / Page Down. Allow keyboard repeat; the engine coalesces it.
        if (!RegisterHotKey(_source.Handle, 1, 0x0003, 0x21) || !RegisterHotKey(_source.Handle, 2, 0x0003, 0x22))
        {
            var error = Marshal.GetLastWin32Error();
            Dispose();
            throw new Win32Exception(error, "Could not register the brightness shortcuts.");
        }
    }

    private IntPtr HandleMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() is 1 or 2)
        {
            handled = true;
            Step?.Invoke(wParam.ToInt32() == 1 ? 5 : -5);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source.IsDisposed) return;
        UnregisterHotKey(_source.Handle, 1);
        UnregisterHotKey(_source.Handle, 2);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
