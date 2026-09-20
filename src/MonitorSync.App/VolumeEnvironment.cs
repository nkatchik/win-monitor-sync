using MonitorSync.Core;
using MonitorSync.Windows;

namespace MonitorSync.App;

// Keep platform I/O replaceable so tests can exercise the actual connection/retry loop.
internal interface IVolumeEnvironment
{
    AudioDeviceInfo DescribeDefault();
    IAudioConnection OpenAudio(string endpointId);
    Task<MonitorDescriptor[]> ListAsync(CancellationToken token);
    IMonitorVolume OpenMonitor(string monitorId, string endpointId);
    void Log(string message);
}

internal sealed class WindowsVolumeEnvironment(DdcClient ddc) : IVolumeEnvironment
{
    public AudioDeviceInfo DescribeDefault() => AudioEndpoint.DescribeDefault();
    public IAudioConnection OpenAudio(string endpointId) => new AudioEndpoint(endpointId);
    public Task<MonitorDescriptor[]> ListAsync(CancellationToken token) => ddc.ListAsync(token);
    public IMonitorVolume OpenMonitor(string monitorId, string endpointId) => new MonitorVolume(ddc, monitorId, endpointId);
    public void Log(string message) => SettingsStore.Log(message);
}
