using MonitorSync.Core;

static class HardwareVolumeTests
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Volume slider input coalesces to the latest absolute level within 20 ms", async () =>
        {
            var f = new Fixture(); await f.Start();
            f.Engine.SetPercent(20); await f.Tick(10); f.Engine.SetPercent(30);
            await f.Tick(19); Equal(0, f.Monitor.Writes.Count); await f.Tick(20);
            Equal(1, f.Monitor.Writes.Count); Equal(30u, f.Monitor.Current);
            await f.Tick(220); Equal(30, f.Engine.MonitorPercent); Equal(100, f.Audio.Percent);
        }),
        ("Volume keys continue from pending slider input", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Engine.SetPercent(25); f.Engine.Step(2);
            await f.Tick(100); await f.Tick(300); Equal(27, f.Engine.MonitorPercent);
        }),
        ("A newer slider request invalidates in-flight volume confirmation", async () =>
        {
            var f = new Fixture(60); await f.Start(); f.Engine.SetPercent(25); await f.Tick(100);
            f.Monitor.OnRead = () => { f.Engine.SetPercent(15); return Task.CompletedTask; };
            await f.Tick(300); f.Monitor.OnRead = null; Equal(60, f.Audio.Percent);
            await f.Tick(400); Equal(60, f.Audio.Percent); await f.Tick(600);
            Equal(15, f.Engine.MonitorPercent); Equal(100, f.Audio.Percent);
        }),
        ("Setting an unchanged volume slider does not write", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Engine.SetPercent(40); await f.Tick(1000);
            Equal(0, f.Monitor.Writes.Count); Equal(false, f.Engine.IsPending);
        }),
        ("A route change rejects pending volume slider input", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Engine.SetPercent(20); f.Audio.Active = false;
            await Throws<IOException>(() => f.Tick(100)); Equal(0, f.Monitor.Writes.Count);
        }),
        ("Hardware mode preserves live monitor volume when Windows is already 100", async () =>
        {
            var f = new Fixture(); await f.Start(); await f.Tick(0);
            Equal(40, f.Engine.MonitorPercent); Equal(100, f.Audio.Percent);
            Equal(0, f.Monitor.Writes.Count); Equal(0, f.Audio.Writes.Count);
        }),
        ("Adoption confirms the lower setting before raising Windows to 100", async () =>
        {
            var f = new Fixture(20, 60); await f.Start();
            await f.Tick(19); Equal(0, f.Monitor.Writes.Count);
            Equal(20, f.Audio.Percent); await f.Tick(20); Equal(20u, f.Monitor.Current);
            await f.Tick(219); Equal(1, f.Monitor.Reads); Equal(20, f.Audio.Percent);
            await f.Tick(220); Equal(2, f.Monitor.Reads); Equal(100, f.Audio.Percent);
            await f.Tick(6000); Equal(20, f.Engine.MonitorPercent); Equal(1, f.Monitor.Writes.Count);
        }),
        ("Equal startup levels still verify write control before pinning", async () =>
        {
            var f = new Fixture(34, 34); await f.Start(); await f.Tick(100);
            Equal(34, f.Audio.Percent); await f.Tick(300); Equal(100, f.Audio.Percent);
            Equal(34, f.Engine.MonitorPercent); Equal(1, f.Monitor.Writes.Count);
        }),
        ("Failed initial DDC read leaves Windows untouched", async () =>
        {
            var f = new Fixture(20); f.Monitor.OnRead = () => throw new IOException("checksum");
            await Throws<IOException>(f.Start); Equal(20, f.Audio.Percent); Equal(0, f.Audio.Writes.Count);
        }),
        ("An unconfirmed write never raises Windows", async () =>
        {
            var f = new Fixture(20); f.Monitor.ApplyWrites = false;
            await f.Start(); await f.Tick(100); await f.Tick(300); await f.Tick(500);
            await Throws<IOException>(() => f.Tick(700)); Equal(20, f.Audio.Percent);
        }),
        ("A DDC write acknowledgement alone cannot raise Windows to 100", async () =>
        {
            var f = new Fixture(20); f.Monitor.ApplyWrites = false;
            await f.Start(); await f.Tick(100);
            Equal(1, f.Monitor.Writes.Count); Equal(0, f.Audio.Writes.Count);
            await f.Tick(300); await f.Tick(500);
            Equal(20, f.Audio.Percent); Equal(0, f.Audio.Writes.Count);
            f.Monitor.Current = 20; await f.Tick(700);
            Equal(100, f.Audio.Percent); Equal(1, f.Audio.Writes.Count);
        }),
        ("Failed DDC readback after a successful set preserves the Windows request", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Audio.UserSet(25);
            await f.Tick(0); await f.Tick(100); Equal(25u, f.Monitor.Current);
            f.Monitor.OnRead = () => throw new IOException("Readback failed");
            await Throws<IOException>(() => f.Tick(300));
            Equal(25, f.Audio.Percent); Equal(0, f.Audio.Writes.Count);
        }),
        ("A failed write never raises Windows", async () =>
        {
            var f = new Fixture(20); await f.Start();
            f.Monitor.OnWrite = () => throw new IOException("I2C");
            await Throws<IOException>(() => f.Tick(100)); Equal(20, f.Audio.Percent);
        }),
        ("Volume keys move only monitor gain", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Engine.Step(-2);
            await f.Tick(19); Equal(0, f.Monitor.Writes.Count);
            await f.Tick(20); Equal(38u, f.Monitor.Current); Equal(40, f.Engine.MonitorPercent);
            await f.Tick(220); Equal(38, f.Engine.MonitorPercent); Equal(100, f.Audio.Percent);
            Equal(0, f.Audio.Writes.Count);
        }),
        ("Held volume keys coalesce without postponing writes", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Engine.Step(2);
            for (var t = 5; t <= 20; t += 5) { f.Now = t; f.Engine.Step(2); await f.Tick(t); }
            Equal(1, f.Monitor.Writes.Count); Equal(50u, f.Monitor.Current);
            for (var t = 25; t <= 45; t += 5) { f.Now = t; f.Engine.Step(2); await f.Tick(t); }
            Equal(2, f.Monitor.Writes.Count); Equal(60u, f.Monitor.Current);
            await f.Tick(245); Equal(60, f.Engine.MonitorPercent); Equal(false, f.Engine.IsPending);
        }),
        ("Keys at either volume limit do not write", async () =>
        {
            foreach (var (raw, delta) in new[] { (0u, -2), (100u, 2) })
            {
                var f = new Fixture(100, raw); await f.Start(); f.Engine.Step(delta); await f.Tick(1000);
                Equal(false, f.Engine.IsPending); Equal(0, f.Monitor.Writes.Count);
            }
        }),
        ("A key during a delayed write supersedes its completion", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Engine.Step(2);
            f.Monitor.OnWrite = () => { f.Engine.Step(2); return Task.CompletedTask; };
            await f.Tick(100); f.Monitor.OnWrite = null; await f.Tick(200); await f.Tick(400);
            Equal(44, f.Engine.MonitorPercent); Equal(100, f.Audio.Percent);
        }),
        ("A key during readback supersedes old confirmation", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Engine.Step(2); await f.Tick(100);
            f.Monitor.OnRead = () => { f.Engine.Step(-2); return Task.CompletedTask; };
            await f.Tick(300); f.Monitor.OnRead = null; Equal(true, f.Engine.IsPending);
            await f.Tick(400); await f.Tick(600); Equal(40, f.Engine.MonitorPercent);
        }),
        ("Native Windows volume requests reach hardware before Windows is restored", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Audio.UserSet(25); await f.Tick(0);
            await f.Tick(100); Equal(25, f.Audio.Percent); await f.Tick(300);
            Equal(25, f.Engine.MonitorPercent); Equal(100, f.Audio.Percent);
            await f.Tick(400); Equal(false, f.Engine.IsPending); Equal(1, f.Monitor.Writes.Count);
        }),
        ("A newer Windows request during confirmation wins", async () =>
        {
            var f = new Fixture(20); await f.Start(); await f.Tick(100);
            f.Monitor.OnRead = () => { f.Audio.UserSet(15); return Task.CompletedTask; };
            await f.Tick(300); f.Monitor.OnRead = null; Equal(15, f.Audio.Percent);
            await f.Tick(400); await f.Tick(600); Equal(15, f.Engine.MonitorPercent); Equal(100, f.Audio.Percent);
        }),
        ("A newer change immediately before pinning is preserved", async () =>
        {
            var f = new Fixture(20); await f.Start(); await f.Tick(100);
            f.Audio.BeforeSet = () => f.Audio.UserSet(10); await f.Tick(300); f.Audio.BeforeSet = null;
            Equal(10, f.Audio.Percent); await f.Tick(400); await f.Tick(600);
            Equal(10, f.Engine.MonitorPercent); Equal(100, f.Audio.Percent);
        }),
        ("A delayed external notification cannot turn our pin into maximum monitor volume", async () =>
        {
            var f = new Fixture(20); await f.Start(); await f.Tick(100);
            f.Audio.AfterSet = () => f.Audio.Revision++; await f.Tick(300);
            await f.Tick(400); await f.Tick(600);
            Equal(20, f.Engine.MonitorPercent); Equal(false, f.Engine.IsPending); Equal(1, f.Monitor.Writes.Count);
        }),
        ("Hardware knob changes never lower Windows volume", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Monitor.Current = 22; await f.Tick(5000);
            Equal(22, f.Engine.MonitorPercent); Equal(100, f.Audio.Percent); Equal(0, f.Audio.Writes.Count);
        }),
        ("Mute is preserved across adoption, keys, and hardware polling", async () =>
        {
            var f = new Fixture(20); f.Audio.Muted = true; await f.Start();
            await f.Tick(100); await f.Tick(300); f.Engine.Step(2); await f.Tick(400); await f.Tick(600);
            f.Monitor.Current = 10; await f.Tick(6000); Equal(true, f.Audio.Muted); Equal(true, f.Engine.Muted);
            Equal(100, f.Audio.Percent);
        }),
        ("An inactive audio output is rejected before any DDC read", async () =>
        {
            var f = new Fixture(); f.Audio.Active = false;
            await Throws<IOException>(f.Start); Equal(0, f.Monitor.Reads);
        }),
        ("An output switch during adoption cannot pin the old endpoint", async () =>
        {
            var f = new Fixture(20); await f.Start(); await f.Tick(100);
            f.Monitor.OnRead = () => { f.Audio.Active = false; return Task.CompletedTask; };
            await Throws<IOException>(() => f.Tick(300)); Equal(20, f.Audio.Percent);
        }),
        ("An output switch before a key write prevents the write", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Engine.Step(2); f.Audio.Active = false;
            await Throws<IOException>(() => f.Tick(100)); Equal(0, f.Monitor.Writes.Count);
        }),
        ("Cancellation during confirmation cannot pin Windows", async () =>
        {
            var f = new Fixture(20); await f.Start(); await f.Tick(100);
            using var cancellation = new CancellationTokenSource(); f.Now = 300;
            f.Monitor.OnRead = () => { cancellation.Cancel(); return Task.CompletedTask; };
            await Throws<OperationCanceledException>(() => f.Engine.TickAsync(cancellation.Token)); Equal(20, f.Audio.Percent);
        }),
        ("Changed monitor ranges invalidate hardware control", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Monitor.Maximum = 200;
            await Throws<IOException>(() => f.Tick(5000)); Equal(100, f.Audio.Percent);
        }),
        ("Quantized hardware readback never quantizes Windows gain", async () =>
        {
            var f = new Fixture(100, 6, 12); await f.Start(); f.Engine.Step(2);
            await f.Tick(100); await f.Tick(300); Equal(50, f.Engine.MonitorPercent); Equal(100, f.Audio.Percent);
        }),
        ("Standard and Apple display-brightness usages are recognized precisely", () =>
        {
            Equal(5, BrightnessUsages.Direction(0x0C, 0x6F, 0)); Equal(-5, BrightnessUsages.Direction(0x0C, 0x70, 0));
            Equal(5, BrightnessUsages.Direction(0xFF01, 0x20, 0x05AC)); Equal(-5, BrightnessUsages.Direction(0xFF01, 0x21, 0x05AC));
            Equal(5, BrightnessUsages.Direction(0x00FF, 4, 0x05AC)); Equal(-5, BrightnessUsages.Direction(0x00FF, 5, 0x05AC));
            foreach (var vendor in new uint[] { 0, 0x046D, 0x1532 }) Equal(0, BrightnessUsages.Direction(0xFF01, 0x20, vendor));
            foreach (var usage in new ushort[] { 0x79, 0x7A, 0xE9, 0xEA }) Equal(0, BrightnessUsages.Direction(0x0C, usage, 0x05AC));
            foreach (var usage in new ushort[] { 3, 7, 8, 9 }) Equal(0, BrightnessUsages.Direction(0x00FF, usage, 0x05AC));
            Equal(0, BrightnessUsages.Direction(7, 0x3A, 0x05AC)); Equal(0, BrightnessUsages.Direction(7, 0x3B, 0x05AC));
            return Task.CompletedTask;
        })
    ];

    private sealed class Fixture(int percent = 100, uint raw = 40, uint maximum = 100)
    {
        public readonly FakeAudio Audio = new(percent);
        public readonly FakeMonitor Monitor = new(raw, maximum);
        public long Now;
        private HardwareVolumeController? _engine;
        public HardwareVolumeController Engine => _engine ??= new(Audio, Monitor, () => Now);
        public Task Start() => Engine.StartAsync(CancellationToken.None);
        public Task Tick(long milliseconds) { Now = milliseconds; return Engine.TickAsync(CancellationToken.None); }
    }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
}
