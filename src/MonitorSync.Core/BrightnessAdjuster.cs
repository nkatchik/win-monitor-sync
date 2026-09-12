namespace MonitorSync.Core;

public sealed class MonitorTargetChangedException() : Exception("The monitor control target changed.");

/// <summary>A single brightness key burst, based on a fresh hardware reading.</summary>
public sealed class BrightnessAdjuster(IMonitorVolume monitor, Func<bool> isTargetCurrent, Func<long> milliseconds)
{
    private readonly List<(int Value, bool Absolute)> _initialInput = [];
    private VolumeReading _confirmed;
    private bool _started, _dirty;
    private int _desired, _confirmationReads;
    private uint? _expected;
    private long _writeDue, _confirmDue;

    public int Percent => _confirmed.Percent;
    public bool HasReading => _started;
    public bool IsPending => !_started || _dirty || _expected.HasValue;

    public void Step(int delta)
    {
        if (delta is not (-5 or 5)) throw new ArgumentOutOfRangeException(nameof(delta));
        if (!_started) { _initialInput.Add((delta, false)); return; }
        Queue(Math.Clamp(_desired + delta, 0, 100));
    }

    public void SetPercent(int percent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percent, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        if (!_started) { _initialInput.Add((percent, true)); return; }
        Queue(percent);
    }

    private void Queue(int desired)
    {
        if (desired == _desired) return;
        _desired = desired;
        if (!_dirty) _writeDue = milliseconds() + 100;
        _dirty = true;
        _expected = null;
    }

    public async Task StartAsync(CancellationToken token)
    {
        if (_started) throw new InvalidOperationException("Create a new brightness burst to read the current level.");
        Guard(token);
        var reading = await monitor.ReadAsync(token);
        Guard(token);
        reading.Validate();
        _confirmed = reading;
        _desired = reading.Percent;
        foreach (var (value, absolute) in _initialInput)
            _desired = absolute ? value : Math.Clamp(_desired + value, 0, 100);
        _initialInput.Clear();
        _started = true;
        _dirty = reading.RawFor(_desired) != reading.Current;
        _writeDue = milliseconds() + 100;
    }

    public async Task TickAsync(CancellationToken token)
    {
        if (!_started) throw new InvalidOperationException("Read the monitor before applying brightness.");
        Guard(token);
        if (_dirty)
        {
            if (milliseconds() < _writeDue) return;
            var desired = _desired;
            var raw = _confirmed.RawFor(desired);
            _dirty = false;
            await monitor.WriteAsync(raw, token);
            Guard(token);
            if (_dirty || _desired != desired) return;
            _expected = raw;
            _confirmationReads = 0;
            _confirmDue = milliseconds() + 200;
            return;
        }
        if (_expected is not uint expected || milliseconds() < _confirmDue) return;
        var reading = await monitor.ReadAsync(token);
        Guard(token);
        reading.Validate();
        if (reading.Maximum != _confirmed.Maximum) throw new IOException("The brightness range changed.");
        if (_dirty || _expected != expected) return;
        if (reading.Current != expected)
        {
            if (++_confirmationReads >= 3) throw new IOException("The monitor did not confirm the brightness change.");
            _confirmDue = milliseconds() + 200;
            return;
        }
        _confirmed = reading;
        _desired = reading.Percent;
        _expected = null;
    }

    private void Guard(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!isTargetCurrent()) throw new MonitorTargetChangedException();
    }
}
