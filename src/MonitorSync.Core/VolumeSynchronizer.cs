namespace MonitorSync.Core;

/// <summary>
/// Single-consumer state machine. Audio may change during any awaited monitor operation.
/// DDC commands never run in an audio callback. The host cancels/discards an instance
/// on pause or topology changes rather than reusing state for a different connection.
/// </summary>
public sealed class VolumeSynchronizer(IAudioVolume audio, IMonitorVolume monitor,
    Func<long> milliseconds, SyncOptions? options = null)
{
    private readonly SyncOptions _options = options ?? new();
    private AudioSnapshot _lastAudio;
    private VolumeReading _lastMonitor;
    private int? _pendingPercent;
    private long _writeDue;
    private uint? _expectedRaw;
    private int _writtenPercent;
    private long _confirmDue;
    private int _confirmationReads;
    private long _pollDue;
    private bool _started;

    public int WindowsPercent => _lastAudio.Percent;
    public int MonitorPercent => _lastMonitor.Percent;
    public bool IsPending => _pendingPercent.HasValue || _expectedRaw.HasValue;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started) throw new InvalidOperationException("Create a new synchronizer to start again.");
        _ = audio.Capture(); // Reject an inactive route before touching the monitor.
        var reading = await monitor.ReadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        reading.Validate();
        var current = audio.Capture();
        var lower = Math.Min(current.Percent, reading.Percent);
        if (current.Percent != lower)
            audio.TrySetPercent(lower, current);
        _lastAudio = audio.Capture();
        _lastMonitor = reading;
        _started = true;
        _pollDue = milliseconds() + _options.PollMs;
        if (reading.Current != reading.RawFor(_lastAudio.Percent))
            Queue(_lastAudio.Percent, milliseconds());
    }

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (!_started) throw new InvalidOperationException("Start before ticking.");
        cancellationToken.ThrowIfCancellationRequested();
        var now = milliseconds();
        Observe(audio.Capture(), now);

        if (_pendingPercent is int percent)
        {
            if (now < _writeDue) return;
            var raw = _lastMonitor.RawFor(percent);
            // Clear only the intent that is being sent. A new input is captured below.
            _pendingPercent = null;
            await monitor.WriteAsync(raw, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _expectedRaw = raw;
            _writtenPercent = percent;
            _confirmationReads = 0;
            _confirmDue = milliseconds() + _options.SettleMs;
            Observe(audio.Capture(), milliseconds());
            return;
        }

        if (_expectedRaw is uint expected)
        {
            if (now < _confirmDue) return;
            var reading = await monitor.ReadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            CheckRange(reading);
            var current = audio.Capture();
            Observe(current, milliseconds());
            if (_pendingPercent.HasValue || current.Percent != _writtenPercent)
                return; // Never turn an old readback into a new Windows request.
            if (reading.Current != expected)
            {
                if (++_confirmationReads >= _options.ConfirmationAttempts)
                    throw new IOException($"Monitor did not confirm volume {expected}/{reading.Maximum}; " +
                        $"it still reports {reading.Current}/{reading.Maximum}. Sync is paused.");
                _confirmDue = milliseconds() + _options.SettleMs;
                return;
            }
            _lastMonitor = reading;
            _expectedRaw = null;
            _pollDue = milliseconds() + _options.PollMs;
            // A small raw range can quantize the requested percentage. Reflect only
            // confirmed quantization, and only if the user has not changed it again.
            if (current.Percent != reading.Percent && audio.TrySetPercent(reading.Percent, current))
                _lastAudio = audio.Capture();
            return;
        }

        if (now < _pollDue) return;
        var before = audio.Capture();
        var observed = await monitor.ReadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        CheckRange(observed);
        var after = audio.Capture();
        Observe(after, milliseconds());
        _pollDue = milliseconds() + _options.PollMs;
        if (_pendingPercent.HasValue || !SameIntent(before, after)) return;
        if (observed.Current != _lastMonitor.Current)
        {
            if (audio.TrySetPercent(observed.Percent, after))
            {
                _lastMonitor = observed;
                _lastAudio = audio.Capture();
            }
        }
    }

    private void Observe(AudioSnapshot snapshot, long now)
    {
        if (snapshot.EndpointId != _lastAudio.EndpointId)
            throw new IOException("The playback device changed. Reconnect the saved pairing to resume.");
        if (snapshot.Percent != _lastAudio.Percent)
            Queue(snapshot.Percent, now + _options.CoalesceMs);
        _lastAudio = snapshot;
    }

    private void Queue(int percent, long due)
    {
        // Keep the first deadline during a burst, so holding a media key makes
        // progress instead of postponing every write until the key is released.
        if (!_pendingPercent.HasValue) _writeDue = due;
        _pendingPercent = percent;
        _expectedRaw = null;
    }

    private void CheckRange(VolumeReading reading)
    {
        reading.Validate();
        if (reading.Maximum != _lastMonitor.Maximum)
            throw new IOException("The monitor's volume range changed. Refresh the display pairing.");
    }

    private static bool SameIntent(AudioSnapshot a, AudioSnapshot b) =>
        a.EndpointId == b.EndpointId && a.Percent == b.Percent && a.Revision == b.Revision;
}
