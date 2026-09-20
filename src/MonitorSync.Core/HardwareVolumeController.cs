namespace MonitorSync.Core;

/// <summary>Matches Windows and monitor volume, preserving newer input during asynchronous DDC work.</summary>
public sealed class HardwareVolumeController(IAudioVolume audio, IMonitorVolume monitor, Func<long> milliseconds)
{
    private AudioSnapshot _lastAudio;
    private VolumeReading _confirmed;
    private int _desired, _attempts;
    private long _writeDue, _confirmDue, _pollDue, _intent;
    private uint? _expected;
    private bool _started, _dirty, _writing;

    public int WindowsPercent => _lastAudio.Percent;
    public int MonitorPercent => _confirmed.Percent;
    public bool Muted => _lastAudio.Muted;
    public bool IsPending => _dirty || _writing || _expected.HasValue;

    public async Task StartAsync(CancellationToken token, bool preserveWindowsVolume = false)
    {
        if (_started) throw new InvalidOperationException("Create a new controller for each audio route.");
        var before = audio.Capture();
        var reading = await monitor.ReadAsync(token);
        token.ThrowIfCancellationRequested();
        reading.Validate();
        var current = audio.Capture();
        GuardRoute(before, current);
        // Adopt the lower live level on a new connection, unless Windows changed during discovery.
        // In particular, migrating from pinned Windows gain must not turn the monitor up to 100%.
        // Same-route DDC recovery keeps the current Windows request, including an undelivered write.
        if (!preserveWindowsVolume && current == before && current.Percent > reading.Percent)
        {
            audio.TrySetPercent(reading.Percent, current);
            current = audio.Capture();
            GuardRoute(before, current);
        }
        _lastAudio = current;
        _confirmed = reading;
        _desired = current.Percent;
        _started = true;
        _pollDue = milliseconds() + 5000;
        if (current.Percent != reading.Percent) Queue(current.Percent);
    }

    public void SetPercent(int percent)
    {
        if (!_started) throw new InvalidOperationException("Read the monitor before accepting volume input.");
        ArgumentOutOfRangeException.ThrowIfLessThan(percent, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        Observe(audio.Capture());
        // Explicit tray input changes Windows immediately; DDC confirmation
        // only settles the hardware value.
        if (percent != _lastAudio.Percent && audio.TrySetPercent(percent, _lastAudio))
        {
            _lastAudio = _lastAudio with { Percent = percent };
            Queue(percent);
        }
        Observe(audio.Capture());
    }

    public async Task TickAsync(CancellationToken token)
    {
        if (!_started) throw new InvalidOperationException("Start before ticking.");
        token.ThrowIfCancellationRequested();
        Observe(audio.Capture());
        if (_dirty)
        {
            if (milliseconds() < _writeDue) return;
            var intent = _intent;
            var raw = _confirmed.RawFor(_desired);
            _dirty = false;
            // Clearing the queued request must not expose stale readback as settled
            // while the write is waiting for the DDC worker or monitor.
            _writing = true;
            try
            {
                await monitor.WriteAsync(raw, token);
                token.ThrowIfCancellationRequested();
                Observe(audio.Capture());
                if (_intent != intent) return;
                _expected = raw;
                _attempts = 0;
                _confirmDue = milliseconds() + 200;
            }
            finally { _writing = false; }
            return;
        }
        if (_expected is uint expected)
        {
            if (milliseconds() < _confirmDue) return;
            var intent = _intent;
            var reading = await monitor.ReadAsync(token);
            token.ThrowIfCancellationRequested();
            CheckRange(reading);
            Observe(audio.Capture());
            if (_intent != intent) return;
            if (reading.Current != expected)
            {
                if (++_attempts >= 3) throw new IOException("The monitor did not confirm the volume change.");
                _confirmDue = milliseconds() + 200;
                return;
            }
            _confirmed = reading;
            _desired = reading.Percent;
            _expected = null;
            _pollDue = milliseconds() + 5000;
            MatchWindowsToConfirmed();
            return;
        }
        if (milliseconds() < _pollDue) return;
        var pollIntent = _intent;
        var beforePoll = _lastAudio;
        var observed = await monitor.ReadAsync(token);
        token.ThrowIfCancellationRequested();
        CheckRange(observed);
        Observe(audio.Capture());
        _pollDue = milliseconds() + 5000;
        if (_intent != pollIntent || _lastAudio != beforePoll) return;
        if (observed.Current == _confirmed.Current) return;
        _confirmed = observed;
        _desired = observed.Percent;
        MatchWindowsToConfirmed();
    }

    private void Observe(AudioSnapshot current)
    {
        GuardRoute(_lastAudio, current);
        // Native keys, sliders, and application endpoint writes are absolute requests.
        if (current.Percent != _lastAudio.Percent) Queue(current.Percent);
        _lastAudio = current;
    }

    private void MatchWindowsToConfirmed()
    {
        if (_lastAudio.Percent == _confirmed.Percent) return;
        // Mirror hardware-button changes and confirmed quantization without
        // overwriting a newer slider, mute, or route change or echoing our own set.
        if (audio.TrySetPercent(_confirmed.Percent, _lastAudio))
            _lastAudio = _lastAudio with { Percent = _confirmed.Percent };
        Observe(audio.Capture());
        if (_lastAudio.Percent != _confirmed.Percent && !IsPending) Queue(_lastAudio.Percent);
    }

    private void Queue(int desired)
    {
        if (!_dirty) _writeDue = milliseconds() + 20;
        _desired = desired;
        _dirty = true;
        _expected = null;
        _intent++;
    }

    private void CheckRange(VolumeReading reading)
    {
        reading.Validate();
        if (reading.Maximum != _confirmed.Maximum) throw new IOException("The monitor's volume range changed.");
    }

    private static void GuardRoute(AudioSnapshot before, AudioSnapshot after)
    {
        if (before.EndpointId != after.EndpointId) throw new IOException("The playback output changed.");
    }
}
