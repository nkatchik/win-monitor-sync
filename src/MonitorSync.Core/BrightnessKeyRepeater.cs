namespace MonitorSync.Core;

/// <summary>Turns HID key states into steps without repeating on unrelated reports.</summary>
public sealed class BrightnessKeyRepeater(Func<long> milliseconds)
{
    private readonly Dictionary<(long Device, byte Report), HeldKey> _held = [];
    public bool HasKeys => _held.Count > 0;

    public int Update(long device, byte report, bool up, bool down)
    {
        var key = (device, report);
        var direction = up == down ? 0 : up ? 5 : -5;
        if (direction == 0) { _held.Remove(key); return 0; }
        if (_held.TryGetValue(key, out var previous) && previous.Direction == direction) return 0;
        _held[key] = new(direction, milliseconds() + 400);
        return direction;
    }

    public int Tick()
    {
        var now = milliseconds();
        var step = 0;
        foreach (var (key, held) in _held.ToArray())
        {
            if (now < held.Due) continue;
            step += held.Direction;
            // A stalled dispatcher sends one step, never a catch-up burst.
            _held[key] = held with { Due = now + 100 };
        }
        return Math.Sign(step) * 5;
    }

    public void Remove(long device)
    {
        foreach (var key in _held.Keys.Where(key => key.Device == device).ToArray()) _held.Remove(key);
    }

    public void Clear() => _held.Clear();
    private sealed record HeldKey(int Direction, long Due);
}
