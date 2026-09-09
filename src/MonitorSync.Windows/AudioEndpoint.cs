using System.Runtime.InteropServices;
using MonitorSync.Core;

namespace MonitorSync.Windows;

public sealed record AudioDeviceInfo(string Id, string Name, int Percent, bool Muted, uint HardwareSupport);
public sealed class AudioRouteChangedException(string message) : IOException(message);

public sealed class AudioEndpoint : IAudioVolume, IDisposable
{
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly IAudioEndpointVolume _volume;
    private readonly VolumeCallback _callback;
    private readonly string _id;
    private bool _disposed;
    private static readonly Guid Context = new("87B84C46-A77C-4650-A9EE-8824BC2B32DD");

    public AudioEndpoint(string expectedId)
    {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            HResult(_enumerator.GetDefaultAudioEndpoint(0, 1, out var device));
            try
            {
                HResult(device.GetId(out _id));
                if (_id != expectedId) throw new AudioRouteChangedException("The default playback device changed.");
                var iid = typeof(IAudioEndpointVolume).GUID;
                HResult(device.Activate(ref iid, 23, IntPtr.Zero, out var activated));
                _volume = (IAudioEndpointVolume)activated;
            }
            finally { Release(device); }
            _callback = new VolumeCallback(Context);
            try { HResult(_volume.RegisterControlChangeNotify(_callback)); }
            catch { Release(_volume); throw; }
        }
        catch { Release(_enumerator); throw; }
    }

    public AudioSnapshot Capture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (DefaultId(_enumerator) != _id)
            throw new AudioRouteChangedException("The default playback device changed. Sync will wait for the paired output.");
        var revision = _callback.Revision;
        HResult(_volume.GetMasterVolumeLevelScalar(out var value));
        HResult(_volume.GetMute(out var muted));
        return new(_id, ToPercent(value), muted, revision);
    }

    public bool TrySetPercent(int percent, AudioSnapshot expected)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percent, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        var current = Capture();
        if (current.EndpointId != expected.EndpointId || current.Percent != expected.Percent ||
            current.Revision != expected.Revision || _callback.Revision != current.Revision)
            return false;
        var context = Context;
        // This does not change endpoint mute or application/session volume.
        HResult(_volume.SetMasterVolumeLevelScalar(percent / 100f, ref context));
        return true;
    }

    public static AudioDeviceInfo DescribeDefault()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            HResult(enumerator.GetDefaultAudioEndpoint(0, 1, out var device));
            try
            {
                HResult(device.GetId(out var id));
                var name = FriendlyName(device) ?? id;
                var iid = typeof(IAudioEndpointVolume).GUID;
                HResult(device.Activate(ref iid, 23, IntPtr.Zero, out var activated));
                var volume = (IAudioEndpointVolume)activated;
                try
                {
                    HResult(volume.GetMasterVolumeLevelScalar(out var scalar));
                    HResult(volume.GetMute(out var muted));
                    HResult(volume.QueryHardwareSupport(out var support));
                    return new(id, name, ToPercent(scalar), muted, support);
                }
                finally { Release(volume); }
            }
            finally { Release(device); }
        }
        finally { Release(enumerator); }
    }

    private static string DefaultId(IMMDeviceEnumerator enumerator)
    {
        HResult(enumerator.GetDefaultAudioEndpoint(0, 1, out var device));
        try { HResult(device.GetId(out var id)); return id; }
        finally { Release(device); }
    }

    private static string? FriendlyName(IMMDevice device)
    {
        HResult(device.OpenPropertyStore(0, out var properties));
        try
        {
            var key = new PropertyKey { FormatId = new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
            HResult(properties.GetValue(ref key, out var value));
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally { Release(properties); }
    }

    private static int ToPercent(float value) => Math.Clamp(
        (int)Math.Round(value * 100, MidpointRounding.AwayFromZero), 0, 100);
    private static void HResult(int hr)
    {
        // E_NOTFOUND during a route transition, or AUDCLNT_E_DEVICE_INVALIDATED.
        if (hr is unchecked((int)0x80070490) or unchecked((int)0x88890004))
            throw new AudioRouteChangedException("The playback output is temporarily unavailable.");
        Marshal.ThrowExceptionForHR(hr);
    }
    private static void Release(object value) { if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _volume.UnregisterControlChangeNotify(_callback);
        Release(_volume);
        Release(_enumerator);
        GC.KeepAlive(_callback);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class VolumeCallback(Guid context) : IAudioEndpointVolumeCallback
    {
        private long _revision;
        public long Revision => Interlocked.Read(ref _revision);
        public int OnNotify(IntPtr data)
        {
            if (data != IntPtr.Zero && Marshal.PtrToStructure<Guid>(data) != context)
                Interlocked.Increment(ref _revision);
            return 0;
        }
    }

    [ComVisible(true), Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolumeCallback { [PreserveSig] int OnNotify(IntPtr data); }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
        [PreserveSig] int VolumeStepUp(ref Guid context);
        [PreserveSig] int VolumeStepDown(ref Guid context);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float minimum, out float maximum, out float increment);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid FormatId; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropertyVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropertyVariant value);
        [PreserveSig] int Commit();
    }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropertyVariant value);
}
