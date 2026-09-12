using MonitorSync.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Brightness media keys step on press and stop on release", () =>
    {
        long now = 0; var keys = new BrightnessKeyRepeater(() => now);
        Equal(5, keys.Update(1, 3, true, false)); Equal(true, keys.HasKeys);
        now = 399; Equal(0, keys.Tick()); now = 400; Equal(5, keys.Tick());
        Equal(0, keys.Update(1, 3, false, false)); now = 1000; Equal(0, keys.Tick()); Equal(false, keys.HasKeys);
        return Task.CompletedTask;
    }),
    ("Repeated HID state reports do not duplicate steps or defer repeat", () =>
    {
        long now = 0; var keys = new BrightnessKeyRepeater(() => now);
        Equal(-5, keys.Update(1, 3, false, true));
        for (now = 20; now < 400; now += 20) Equal(0, keys.Update(1, 3, false, true));
        Equal(-5, keys.Tick()); now = 499; Equal(0, keys.Tick()); now = 500; Equal(-5, keys.Tick());
        return Task.CompletedTask;
    }),
    ("Unrelated HID report IDs cannot release a held brightness key", () =>
    {
        long now = 0; var keys = new BrightnessKeyRepeater(() => now);
        keys.Update(1, 3, true, false); keys.Update(1, 4, false, false);
        now = 400; Equal(5, keys.Tick());
        return Task.CompletedTask;
    }),
    ("Brightness media key reversal and conflicting keys are handled", () =>
    {
        long now = 0; var keys = new BrightnessKeyRepeater(() => now);
        Equal(5, keys.Update(1, 3, true, false)); now = 100;
        Equal(-5, keys.Update(1, 3, false, true)); now = 400; Equal(0, keys.Tick());
        now = 500; Equal(-5, keys.Tick()); keys.Update(1, 3, true, true);
        now = 1000; Equal(0, keys.Tick());
        return Task.CompletedTask;
    }),
    ("Removing a keyboard or suspending clears brightness repeats", () =>
    {
        long now = 0; var keys = new BrightnessKeyRepeater(() => now);
        keys.Update(1, 3, true, false); keys.Update(2, 3, false, true); keys.Remove(1);
        now = 400; Equal(-5, keys.Tick()); keys.Clear(); now = 800; Equal(0, keys.Tick());
        return Task.CompletedTask;
    }),
    ("A delayed dispatcher does not produce a brightness catch-up burst", () =>
    {
        long now = 0; var keys = new BrightnessKeyRepeater(() => now);
        keys.Update(1, 3, true, false); now = 10000; Equal(5, keys.Tick()); Equal(0, keys.Tick());
        return Task.CompletedTask;
    }),
    ("Brightness reads live hardware and only displays confirmed changes", async () =>
    {
        var f = new BrightnessFixture(); f.Engine.Step(-5); await f.Start();
        Equal(40, f.Engine.Percent); Equal(0, f.Monitor.Writes.Count);
        await f.Tick(99); Equal(0, f.Monitor.Writes.Count);
        await f.Tick(100); Equal(35u, f.Monitor.Current); Equal(40, f.Engine.Percent);
        await f.Tick(300); Equal(35, f.Engine.Percent); Equal(false, f.Engine.IsPending);
    }),
    ("Brightness input during discovery preserves clamping and reversal order", async () =>
    {
        var f = new BrightnessFixture(100);
        f.Engine.Step(5);
        f.Monitor.OnRead = () => { f.Engine.Step(-5); return Task.CompletedTask; };
        await f.Start(); f.Monitor.OnRead = null; await f.Tick(100); await f.Tick(300);
        Equal(95, f.Engine.Percent); Equal(1, f.Monitor.Writes.Count);
    }),
    ("Brightness at either limit does not write", async () =>
    {
        foreach (var (raw, delta) in new[] { (0u, -5), (100u, 5) })
        {
            var f = new BrightnessFixture(raw); f.Engine.Step(delta); await f.Start(); await f.Tick(1000);
            Equal(false, f.Engine.IsPending); Equal(0, f.Monitor.Writes.Count);
        }
    }),
    ("Held brightness keys coalesce without postponing the deadline", async () =>
    {
        var f = new BrightnessFixture(); f.Engine.Step(5); await f.Start();
        for (var t = 20; t <= 100; t += 20) { f.Engine.Step(5); await f.Tick(t); }
        Equal(1, f.Monitor.Writes.Count); Equal(70u, f.Monitor.Current);
        await f.Tick(300); Equal(70, f.Engine.Percent);
    }),
    ("Brightness input during a delayed write supersedes its completion", async () =>
    {
        var f = new BrightnessFixture(); f.Engine.Step(5); await f.Start();
        f.Monitor.OnWrite = () => { f.Engine.Step(5); return Task.CompletedTask; };
        await f.Tick(100); f.Monitor.OnWrite = null;
        await f.Tick(200); await f.Tick(400);
        Equal(50, f.Engine.Percent); Equal(2, f.Monitor.Writes.Count);
    }),
    ("Brightness input during confirmation supersedes old readback", async () =>
    {
        var f = new BrightnessFixture(); f.Engine.Step(5); await f.Start(); await f.Tick(100);
        f.Monitor.OnRead = () => { f.Engine.Step(-5); return Task.CompletedTask; };
        await f.Tick(300); f.Monitor.OnRead = null;
        Equal(true, f.Engine.IsPending); await f.Tick(400); await f.Tick(600);
        Equal(40, f.Engine.Percent); Equal(40u, f.Monitor.Current);
    }),
    ("Moving the cursor before a brightness write discards the request", async () =>
    {
        var f = new BrightnessFixture(); f.Engine.Step(-5); await f.Start(); f.TargetCurrent = false;
        await Throws<MonitorTargetChangedException>(() => f.Tick(100)); Equal(0, f.Monitor.Writes.Count);
    }),
    ("Moving the cursor during brightness discovery prevents any write", async () =>
    {
        var f = new BrightnessFixture(); f.Engine.Step(-5);
        f.Monitor.OnRead = () => { f.TargetCurrent = false; return Task.CompletedTask; };
        await Throws<MonitorTargetChangedException>(f.Start); Equal(false, f.Engine.HasReading);
        Equal(0, f.Monitor.Writes.Count);
    }),
    ("Moving the cursor during brightness readback discards the old result", async () =>
    {
        var f = new BrightnessFixture(); f.Engine.Step(-5); await f.Start(); await f.Tick(100);
        f.Monitor.OnRead = () => { f.TargetCurrent = false; return Task.CompletedTask; };
        await Throws<MonitorTargetChangedException>(() => f.Tick(300)); Equal(40, f.Engine.Percent);
    }),
    ("Cancellation during brightness discovery prevents any write", async () =>
    {
        var f = new BrightnessFixture(); f.Engine.Step(-5);
        using var cts = new CancellationTokenSource();
        f.Monitor.OnRead = () => { cts.Cancel(); return Task.CompletedTask; };
        await Throws<OperationCanceledException>(() => f.Engine.StartAsync(cts.Token));
        Equal(0, f.Monitor.Writes.Count); Equal(false, f.Engine.HasReading);
    }),
    ("Unconfirmed brightness fails after bounded reads without reporting success", async () =>
    {
        var f = new BrightnessFixture(); f.Monitor.ApplyWrites = false; f.Engine.Step(-5);
        await f.Start(); await f.Tick(100); await f.Tick(300); await f.Tick(500);
        await Throws<IOException>(() => f.Tick(700)); Equal(40, f.Engine.Percent);
        Equal(1, f.Monitor.Writes.Count);
    }),
    ("Brightness displays actual hardware quantization", async () =>
    {
        var f = new BrightnessFixture(15, 30); f.Engine.Step(5); await f.Start();
        await f.Tick(100); await f.Tick(300);
        Equal(17u, f.Monitor.Current); Equal(57, f.Engine.Percent);
    }),
    ("Brightness range changes and read errors cannot confirm a write", async () =>
    {
        var f = new BrightnessFixture(); f.Engine.Step(-5); await f.Start(); await f.Tick(100);
        f.Monitor.Maximum = 200;
        await Throws<IOException>(() => f.Tick(300)); Equal(40, f.Engine.Percent);
        f.Monitor.OnRead = () => throw new IOException("Disconnected");
        await Throws<IOException>(() => f.Tick(500)); Equal(40, f.Engine.Percent);
    }),
    ("Automatically matches display audio to its monitor model", () =>
    {
        var monitor = new MonitorDescriptor("dell", "Dell S2725QS(HDMI1)", new(45, 100), null, null);
        Equal(monitor, AutomaticMonitorSelection.Find("2- DELL S2725QS", true, [monitor]));
        return Task.CompletedTask;
    }),
    ("Headphones cannot be selected even with a matching display name", () =>
    {
        var monitor = new MonitorDescriptor("dell", "Dell S2725QS", new(45, 100), null, null);
        Equal<MonitorDescriptor?>(null, AutomaticMonitorSelection.Find("DELL S2725QS", false, [monitor]));
        return Task.CompletedTask;
    }),
    ("A different display audio output does not control the Dell", () =>
    {
        var monitor = new MonitorDescriptor("dell", "Dell S2725QS(HDMI1)", new(45, 100), null, null);
        Equal<MonitorDescriptor?>(null, AutomaticMonitorSelection.Find("DELL S2725Q", true, [monitor]));
        return Task.CompletedTask;
    }),
    ("Duplicate monitor models prevent automatic selection", () =>
    {
        var first = new MonitorDescriptor("first", "Dell S2725QS(HDMI1)", new(45, 100), null, null);
        var second = first with { Id = "second", Name = "DELL S2725QS(DisplayPort)" };
        Equal<MonitorDescriptor?>(null, AutomaticMonitorSelection.Find("DELL S2725QS", true, [first, second]));
        return Task.CompletedTask;
    }),
    ("An unreadable duplicate does not make a pairing unambiguous", () =>
    {
        var first = new MonitorDescriptor("first", "Dell S2725QS(HDMI1)", new(45, 100), null, null);
        var second = first with { Id = "second", Volume = null };
        Equal<MonitorDescriptor?>(null, AutomaticMonitorSelection.Find("DELL S2725QS", true, [first, second]));
        return Task.CompletedTask;
    }),
    ("A monitor without volume support cannot be selected", () =>
    {
        var monitor = new MonitorDescriptor("dell", "Dell S2725QS", null, new(100, 100), "DDC unavailable");
        Equal<MonitorDescriptor?>(null, AutomaticMonitorSelection.Find("DELL S2725QS", true, [monitor]));
        return Task.CompletedTask;
    }),
    ("Missing endpoint descriptions do not guess a monitor", () =>
    {
        var monitor = new MonitorDescriptor("dell", "Dell S2725QS", new(45, 100), null, null);
        Equal<MonitorDescriptor?>(null, AutomaticMonitorSelection.Find(null, true, [monitor]));
        Equal<MonitorDescriptor?>(null, AutomaticMonitorSelection.Find("", true, [monitor]));
        return Task.CompletedTask;
    }),
    ("Distinct monitor models select only the matching display", () =>
    {
        var dell = new MonitorDescriptor("dell", "Dell S2725QS(DP)", new(45, 100), null, null);
        var other = new MonitorDescriptor("other", "LG 27UP850", new(30, 100), null, null);
        Equal(dell, AutomaticMonitorSelection.Find("DELL S2725QS", true, [other, dell]));
        return Task.CompletedTask;
    }),
    ("Startup lowers Windows instead of raising the monitor", async () =>
    {
        var f = new Fixture(80, 40); await f.Start();
        Equal(40, f.Audio.Percent); Equal(0, f.Monitor.Writes.Count);
    }),
    ("Startup lowers the monitor instead of raising Windows", async () =>
    {
        var f = new Fixture(20, 60); await f.Start(); await f.Tick(0); await f.Tick(200);
        Equal(20, f.Audio.Percent); Equal(20u, f.Monitor.Current); Equal(0, f.Audio.Writes.Count);
    }),
    ("Startup at equal levels does not write", async () =>
    {
        var f = new Fixture(); await f.Start(); await f.Tick(0);
        Equal(0, f.Monitor.Writes.Count); Equal(0, f.Audio.Writes.Count);
    }),
    ("Windows change is coalesced then confirmed", async () =>
    {
        var f = new Fixture(); await f.Start(); f.Audio.UserSet(65);
        await f.Tick(0); Equal(0, f.Monitor.Writes.Count);
        await f.Tick(100); Equal(65u, f.Monitor.Current); Equal(true, f.Engine.IsPending);
        await f.Tick(300); Equal(false, f.Engine.IsPending); Equal(65, f.Engine.MonitorPercent);
    }),
    ("Rapid slider changes send only the newest pending value", async () =>
    {
        var f = new Fixture(); await f.Start();
        f.Audio.UserSet(50); await f.Tick(0); f.Audio.UserSet(70); await f.Tick(50);
        f.Audio.UserSet(80); await f.Tick(100);
        Equal(1, f.Monitor.Writes.Count); Equal(80u, f.Monitor.Writes[0]);
    }),
    ("Held media keys do not postpone the write deadline forever", async () =>
    {
        var f = new Fixture(); await f.Start();
        for (var t = 0; t <= 100; t += 20) { f.Audio.UserSet(50 + t / 20); await f.Tick(t); }
        Equal(1, f.Monitor.Writes.Count); Equal(55u, f.Monitor.Current);
    }),
    ("Physical monitor change updates Windows without an echo", async () =>
    {
        var f = new Fixture(); await f.Start(); f.Monitor.Current = 25;
        await f.Tick(5000); await f.Tick(5100); await f.Tick(10000);
        Equal(25, f.Audio.Percent); Equal(1, f.Audio.Writes.Count); Equal(0, f.Monitor.Writes.Count);
    }),
    ("Windows mute survives a monitor-button update", async () =>
    {
        var f = new Fixture(); f.Audio.Muted = true; await f.Start();
        f.Monitor.Current = 20; await f.Tick(5000); Equal(true, f.Audio.Muted);
    }),
    ("A delayed write response cannot undo newer Windows input", async () =>
    {
        var f = new Fixture(); await f.Start();
        f.Audio.UserSet(60); await f.Tick(0);
        f.Monitor.OnWrite = () => { f.Audio.UserSet(80); return Task.CompletedTask; };
        await f.Tick(100); f.Monitor.OnWrite = null;
        Equal(80, f.Audio.Percent); await f.Tick(200); await f.Tick(400);
        Equal(80u, f.Monitor.Current); Equal(0, f.Audio.Writes.Count);
    }),
    ("New input during confirmation supersedes old readback", async () =>
    {
        var f = new Fixture(); await f.Start(); f.Audio.UserSet(60); await f.Tick(0); await f.Tick(100);
        f.Monitor.OnRead = () => { f.Audio.UserSet(90); return Task.CompletedTask; };
        await f.Tick(300); f.Monitor.OnRead = null; await f.Tick(400); await f.Tick(600);
        Equal(90, f.Audio.Percent); Equal(90u, f.Monitor.Current); Equal(0, f.Audio.Writes.Count);
    }),
    ("New input during idle polling wins over a monitor observation", async () =>
    {
        var f = new Fixture(); await f.Start(); f.Monitor.Current = 20;
        f.Monitor.OnRead = () => { f.Audio.UserSet(70); return Task.CompletedTask; };
        await f.Tick(5000); f.Monitor.OnRead = null; await f.Tick(5100); await f.Tick(5300);
        Equal(70, f.Audio.Percent); Equal(70u, f.Monitor.Current); Equal(0, f.Audio.Writes.Count);
    }),
    ("A volume change away and back during polling is still detected", async () =>
    {
        var f = new Fixture(); await f.Start(); f.Monitor.Current = 20;
        f.Monitor.OnRead = () => { f.Audio.UserSet(70); f.Audio.UserSet(40); return Task.CompletedTask; };
        await f.Tick(5000); Equal(40, f.Audio.Percent); Equal(0, f.Audio.Writes.Count);
    }),
    ("Delayed hardware settling does not roll Windows back", async () =>
    {
        var f = new Fixture(); await f.Start(); f.Monitor.ApplyWrites = false;
        f.Audio.UserSet(60); await f.Tick(0); await f.Tick(100); await f.Tick(300);
        Equal(60, f.Audio.Percent); Equal(0, f.Audio.Writes.Count);
        f.Monitor.Current = 60; await f.Tick(500); Equal(false, f.Engine.IsPending);
    }),
    ("Unconfirmed writes fail after bounded reads without resetting Windows", async () =>
    {
        var f = new Fixture(); await f.Start(); f.Monitor.ApplyWrites = false;
        f.Audio.UserSet(60); await f.Tick(0); await f.Tick(100); await f.Tick(300); await f.Tick(500);
        await Throws<IOException>(() => f.Tick(700));
        Equal(60, f.Audio.Percent); Equal(1, f.Monitor.Writes.Count); Equal(0, f.Audio.Writes.Count);
    }),
    ("Readback normalizes a monitor whose range is not 100", async () =>
    {
        var f = new Fixture(50, 6, 12); await f.Start(); f.Audio.UserSet(60);
        await f.Tick(0); await f.Tick(100); await f.Tick(300); await f.Tick(500);
        Equal(7u, f.Monitor.Current); Equal(58, f.Audio.Percent); Equal(1, f.Monitor.Writes.Count);
    }),
    ("Range changes invalidate the connection", async () =>
    {
        var f = new Fixture(); await f.Start(); f.Monitor.Maximum = 200;
        await Throws<IOException>(() => f.Tick(5000)); Equal(0, f.Audio.Writes.Count);
    }),
    ("Cancellation after a monitor read prevents an audio write", async () =>
    {
        var f = new Fixture(); await f.Start(); f.Monitor.Current = 20;
        using var cts = new CancellationTokenSource();
        f.Monitor.OnRead = () => { cts.Cancel(); return Task.CompletedTask; };
        f.Now = 5000; await Throws<OperationCanceledException>(() => f.Engine.TickAsync(cts.Token));
        Equal(40, f.Audio.Percent); Equal(0, f.Audio.Writes.Count);
    }),
    ("Switching output during a read prevents writes to the other device", async () =>
    {
        var f = new Fixture(); await f.Start(); f.Monitor.Current = 20;
        f.Monitor.OnRead = () => { f.Audio.Active = false; return Task.CompletedTask; };
        await Throws<IOException>(() => f.Tick(5000)); Equal(0, f.Audio.Writes.Count);
    }),
    ("Compare-before-set preserves a last-moment external audio change", async () =>
    {
        var f = new Fixture(); await f.Start(); f.Monitor.Current = 20;
        f.Audio.BeforeSet = () => f.Audio.UserSet(80);
        await f.Tick(5000); Equal(80, f.Audio.Percent); Equal(0, f.Audio.Writes.Count);
    }),
    ("Disconnected audio rejects startup before any DDC operation", async () =>
    {
        var f = new Fixture(); f.Audio.Active = false;
        await Throws<IOException>(f.Start); Equal(0, f.Monitor.Reads);
    }),
    ("Invalid monitor ranges are rejected", () =>
    {
        ThrowsSync<IOException>(() => new VolumeReading(0, 0).Validate());
        ThrowsSync<IOException>(() => new VolumeReading(101, 100).Validate());
        ThrowsSync<IOException>(() => new VolumeReading(0, 65536).Validate());
        return Task.CompletedTask;
    }),
    ("Raw conversion handles endpoints and half-step rounding", () =>
    {
        var r = new VolumeReading(0, 30);
        Equal(0u, r.RawFor(0)); Equal(30u, r.RawFor(100)); Equal(8u, r.RawFor(25));
        Equal(27, new VolumeReading(8, 30).Percent);
        ThrowsSync<ArgumentOutOfRangeException>(() => r.RawFor(101));
        return Task.CompletedTask;
    })
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failures++; Console.Error.WriteLine($"FAIL {name}: {e}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}; got {actual}.");
}
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static void ThrowsSync<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

sealed class Fixture(int percent = 40, uint raw = 40, uint maximum = 100)
{
    public readonly FakeAudio Audio = new(percent);
    public readonly FakeMonitor Monitor = new(raw, maximum);
    public long Now;
    private VolumeSynchronizer? _engine;
    public VolumeSynchronizer Engine => _engine ??= new(Audio, Monitor, () => Now);
    public Task Start() => Engine.StartAsync(CancellationToken.None);
    public Task Tick(long milliseconds) { Now = milliseconds; return Engine.TickAsync(CancellationToken.None); }
}

sealed class BrightnessFixture(uint raw = 40, uint maximum = 100)
{
    public readonly FakeMonitor Monitor = new(raw, maximum);
    public long Now;
    public bool TargetCurrent = true;
    private BrightnessAdjuster? _engine;
    public BrightnessAdjuster Engine => _engine ??= new(Monitor, () => TargetCurrent, () => Now);
    public Task Start() => Engine.StartAsync(CancellationToken.None);
    public Task Tick(long milliseconds) { Now = milliseconds; return Engine.TickAsync(CancellationToken.None); }
}

sealed class FakeAudio(int percent) : IAudioVolume
{
    public int Percent = percent;
    public bool Muted, Active = true;
    public long Revision;
    public readonly List<int> Writes = [];
    public Action? BeforeSet;
    public void UserSet(int value) { Percent = value; Revision++; }
    public AudioSnapshot Capture()
    {
        if (!Active) throw new IOException("Output changed.");
        return new("paired-output", Percent, Muted, Revision);
    }
    public bool TrySetPercent(int value, AudioSnapshot expected)
    {
        BeforeSet?.Invoke();
        if (!Active || Capture() != expected) return false;
        Percent = value; Writes.Add(value); return true;
    }
}

sealed class FakeMonitor(uint raw, uint maximum) : IMonitorVolume
{
    public uint Current = raw, Maximum = maximum;
    public int Reads;
    public bool ApplyWrites = true;
    public Func<Task>? OnRead, OnWrite;
    public readonly List<uint> Writes = [];
    public async Task<VolumeReading> ReadAsync(CancellationToken cancellationToken)
    {
        Reads++; if (OnRead is not null) await OnRead(); return new(Current, Maximum);
    }
    public async Task WriteAsync(uint value, CancellationToken cancellationToken)
    {
        Writes.Add(value); if (ApplyWrites) Current = value;
        if (OnWrite is not null) await OnWrite();
    }
}
