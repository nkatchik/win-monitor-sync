namespace MonitorSync.Core;

/// <summary>Monitor gain is authoritative. Windows gain is restored only after confirmed DDC control.</summary>
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

    public async Task StartAsync(CancellationToken token)
    {
        if (_started) throw new InvalidOperationException("Create a new controller for each audio route.");
        var before = audio.Capture();
        var reading = await monitor.ReadAsync(token);
        token.ThrowIfCancellationRequested();
        reading.Validate();
        var current = audio.Capture();
        GuardRoute(before, current);
        _lastAudio = current;
        _confirmed = reading;
        _desired = reading.Percent;
        _started = true;
        _pollDue = milliseconds() + 5000;
        // Confirm write support at the existing (or lower) setting before raising Windows gain.
        if (current.Percent < 100) Queue(Math.Min(current.Percent, reading.Percent));
    }

    public void Step(int delta)
    {
        if (!_started) throw new InvalidOperationException("Read the monitor before accepting volume keys.");
        if (delta is not (-2 or 2)) throw new ArgumentOutOfRangeException(nameof(delta));
        var desired = Math.Clamp(_desired + delta, 0, 100);
        if (desired != _desired) Queue(desired);
    }

    public void SetPercent(int percent)
    {
        if (!_started) throw new InvalidOperationException("Read the monitor before accepting volume input.");
        ArgumentOutOfRangeException.ThrowIfLessThan(percent, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        if (percent != _desired) Queue(percent);
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
            if (_lastAudio.Percent != 100)
            {
                // The compare-before-set preserves newer slider/mute/route changes during DDC I/O.
                if (audio.TrySetPercent(100, _lastAudio))
                    _lastAudio = _lastAudio with { Percent = 100 };
                Observe(audio.Capture());
                if (_lastAudio.Percent != 100 && !IsPending) Queue(_lastAudio.Percent);
            }
            return;
        }
        if (milliseconds() < _pollDue) return;
        var pollIntent = _intent;
        var observed = await monitor.ReadAsync(token);
        token.ThrowIfCancellationRequested();
        CheckRange(observed);
        Observe(audio.Capture());
        _pollDue = milliseconds() + 5000;
        if (_intent != pollIntent) return;
        _confirmed = observed;
        _desired = observed.Percent;
    }

    private void Observe(AudioSnapshot current)
    {
        GuardRoute(_lastAudio, current);
        // Native sliders and application endpoint writes become absolute hardware requests.
        // Our own restoration to 100 must never become a request for maximum monitor gain.
        if (current.Percent != _lastAudio.Percent && current.Percent != 100) Queue(current.Percent);
        else if (current.Percent == 100 && _lastAudio.Percent != 100 &&
                 current.Revision != _lastAudio.Revision) Queue(100);
        _lastAudio = current;
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
