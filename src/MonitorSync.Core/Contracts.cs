namespace MonitorSync.Core;

public readonly record struct VolumeReading(uint Current, uint Maximum)
{
    public int Percent
    {
        get
        {
            Validate();
            return (int)Math.Round(100.0 * Current / Maximum, MidpointRounding.AwayFromZero);
        }
    }

    public uint RawFor(int percent)
    {
        Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(percent, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        return (uint)Math.Round(percent * Maximum / 100.0, MidpointRounding.AwayFromZero);
    }

    public void Validate()
    {
        if (Maximum is 0 or > 65535 || Current > Maximum)
            throw new IOException($"The monitor returned an invalid range: {Current}/{Maximum}.");
    }
}

public readonly record struct AudioSnapshot(string EndpointId, int Percent, bool Muted, long Revision);

public interface IAudioVolume
{
    // Throws if this endpoint is no longer the default playback device.
    AudioSnapshot Capture();
    // Preserve a newer external change, including an output switch.
    bool TrySetPercent(int percent, AudioSnapshot expected);
}

public interface IMonitorVolume
{
    Task<VolumeReading> ReadAsync(CancellationToken cancellationToken);
    Task WriteAsync(uint rawValue, CancellationToken cancellationToken);
}

public sealed record SyncOptions(int CoalesceMs = 100, int SettleMs = 200,
    int PollMs = 5000, int ConfirmationAttempts = 3);

public sealed record MonitorDescriptor(string Id, string Name,
    VolumeReading? Volume, VolumeReading? Brightness, string? Error);

public sealed record WorkerRequest(string Operation, string? MonitorId = null, byte Code = 0, uint Value = 0);
public sealed record WorkerResponse(bool Success, string? Error = null,
    MonitorDescriptor[]? Monitors = null, VolumeReading? Reading = null);
