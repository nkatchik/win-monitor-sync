using MonitorSync.Core;

static class HardwareVolumeTests
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Volume remains pending throughout a delayed write and confirmation", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Engine.SetPercent(10);
            Equal(10, f.Audio.Percent);
            var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Monitor.OnWrite = () => write.Task;
            var writing = f.Tick(20);
            try
            {
                Equal(false, writing.IsCompleted); Equal(true, f.Engine.IsPending);
                Equal(40, f.Engine.MonitorPercent); Equal(10, f.Audio.Percent);
            }
            finally { write.TrySetResult(); await writing; }
            Equal(true, f.Engine.IsPending); Equal(40, f.Engine.MonitorPercent);
            var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Monitor.OnRead = () => read.Task;
            var confirming = f.Tick(220);
            try
            {
                Equal(false, confirming.IsCompleted); Equal(true, f.Engine.IsPending);
                Equal(40, f.Engine.MonitorPercent); Equal(10, f.Audio.Percent);
            }
            finally { read.TrySetResult(); await confirming; }
            Equal(false, f.Engine.IsPending); Equal(10, f.Engine.MonitorPercent); Equal(10, f.Audio.Percent);
            Equal(1, f.Audio.Writes.Count);
        }),
        ("A superseded tray write stays pending until the latest value is confirmed", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Engine.SetPercent(10);
            f.Monitor.OnWrite = () =>
            {
                Equal(true, f.Engine.IsPending); f.Engine.SetPercent(80);
                return Task.CompletedTask;
            };
            await f.Tick(20); f.Monitor.OnWrite = null;
            Equal(true, f.Engine.IsPending); Equal(40, f.Engine.MonitorPercent); Equal(80, f.Audio.Percent);
            await f.Tick(40); Equal(true, f.Engine.IsPending);
            await f.Tick(240); Equal(false, f.Engine.IsPending); Equal(80, f.Engine.MonitorPercent);
        }),
        ("Failed and canceled writes preserve the Windows request and clear in-flight state", async () =>
        {
            foreach (var cancel in new[] { false, true })
            {
                var f = new Fixture(); await f.Start(); f.Engine.SetPercent(10);
                using var cancellation = new CancellationTokenSource();
                f.Monitor.OnWrite = () =>
                {
                    Equal(true, f.Engine.IsPending);
                    if (!cancel) throw new IOException("Write failed");
                    cancellation.Cancel(); return Task.CompletedTask;
                };
                f.Now = 20;
                if (cancel) await Throws<OperationCanceledException>(() => f.Engine.TickAsync(cancellation.Token));
                else await Throws<IOException>(() => f.Engine.TickAsync(cancellation.Token));
                Equal(false, f.Engine.IsPending); Equal(40, f.Engine.MonitorPercent); Equal(10, f.Audio.Percent);
                Equal(1, f.Audio.Writes.Count);
            }
        }),
        ("Tray input updates Windows immediately and coalesces DDC within 20 ms", async () =>
        {
            var f = new Fixture(); await f.Start();
            f.Engine.SetPercent(20); Equal(20, f.Audio.Percent);
            await f.Tick(10); f.Engine.SetPercent(30); Equal(30, f.Audio.Percent);
            await f.Tick(19); Equal(0, f.Monitor.Writes.Count); await f.Tick(20);
            Equal(1, f.Monitor.Writes.Count); Equal(30u, f.Monitor.Current);
            await f.Tick(220); Equal(30, f.Engine.MonitorPercent); Equal(30, f.Audio.Percent);
            await f.Tick(6000); Equal(1, f.Monitor.Writes.Count); Equal(2, f.Audio.Writes.Count);
        }),
        ("Native key input continues from the tray's immediate Windows level", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Engine.SetPercent(25);
            f.Audio.UserSet(f.Audio.Percent + 2);
            await f.Tick(20); await f.Tick(220);
            Equal(27, f.Engine.MonitorPercent); Equal(27, f.Audio.Percent);
        }),
        ("A newer tray request invalidates in-flight volume confirmation", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Engine.SetPercent(25); await f.Tick(20);
            f.Monitor.OnRead = () => { f.Engine.SetPercent(15); return Task.CompletedTask; };
            await f.Tick(220); f.Monitor.OnRead = null;
            Equal(true, f.Engine.IsPending); Equal(15, f.Audio.Percent); Equal(40, f.Engine.MonitorPercent);
            await f.Tick(240); await f.Tick(440);
            Equal(15, f.Engine.MonitorPercent); Equal(15, f.Audio.Percent);
        }),
        ("An unchanged tray level does not write or reset confirmation", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Engine.SetPercent(40); await f.Tick(1000);
            Equal(0, f.Monitor.Writes.Count); Equal(0, f.Audio.Writes.Count); Equal(false, f.Engine.IsPending);
            f.Engine.SetPercent(20); await f.Tick(1020); f.Engine.SetPercent(20); await f.Tick(1220);
            Equal(1, f.Monitor.Writes.Count); Equal(false, f.Engine.IsPending); Equal(20, f.Engine.MonitorPercent);
        }),
        ("A route change rejects tray input before changing either volume", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Audio.Active = false;
            await Throws<IOException>(() => { f.Engine.SetPercent(20); return Task.CompletedTask; });
            Equal(0, f.Monitor.Writes.Count); Equal(0, f.Audio.Writes.Count);
        }),
        ("Migration lowers pinned Windows gain to the live monitor level", async () =>
        {
            var f = new Fixture(100); await f.Start(); await f.Tick(0);
            Equal(40, f.Engine.MonitorPercent); Equal(40, f.Audio.Percent);
            Equal(0, f.Monitor.Writes.Count); Equal(1, f.Audio.Writes.Count); Equal(false, f.Engine.IsPending);
        }),
        ("Startup with lower Windows gain applies that level without raising Windows", async () =>
        {
            var f = new Fixture(20, 60); await f.Start();
            await f.Tick(19); Equal(0, f.Monitor.Writes.Count);
            Equal(20, f.Audio.Percent); await f.Tick(20); Equal(20u, f.Monitor.Current);
            await f.Tick(219); Equal(1, f.Monitor.Reads); Equal(true, f.Engine.IsPending);
            await f.Tick(220); Equal(2, f.Monitor.Reads); Equal(20, f.Audio.Percent);
            await f.Tick(6000); Equal(20, f.Engine.MonitorPercent); Equal(1, f.Monitor.Writes.Count);
            Equal(0, f.Audio.Writes.Count);
        }),
        ("Equal startup levels require no writes", async () =>
        {
            var f = new Fixture(34, 34); await f.Start(); await f.Tick(6000);
            Equal(34, f.Audio.Percent); Equal(34, f.Engine.MonitorPercent);
            Equal(0, f.Monitor.Writes.Count); Equal(0, f.Audio.Writes.Count);
        }),
        ("A Windows change during discovery wins over the lower startup level", async () =>
        {
            var f = new Fixture(100);
            f.Monitor.OnRead = () => { f.Audio.UserSet(55); return Task.CompletedTask; };
            await f.Start(); f.Monitor.OnRead = null; await f.Tick(20); await f.Tick(220);
            Equal(55, f.Audio.Percent); Equal(55, f.Engine.MonitorPercent); Equal(0, f.Audio.Writes.Count);
        }),
        ("A Windows change immediately before startup alignment is preserved", async () =>
        {
            var f = new Fixture(100); f.Audio.BeforeSet = () => f.Audio.UserSet(25);
            await f.Start(); f.Audio.BeforeSet = null; await f.Tick(20); await f.Tick(220);
            Equal(25, f.Audio.Percent); Equal(25, f.Engine.MonitorPercent); Equal(0, f.Audio.Writes.Count);
        }),
        ("Failed initial DDC read leaves Windows untouched", async () =>
        {
            var f = new Fixture(100); f.Monitor.OnRead = () => throw new IOException("checksum");
            await Throws<IOException>(f.Start); Equal(100, f.Audio.Percent); Equal(0, f.Audio.Writes.Count);
        }),
        ("Canceled discovery cannot align Windows to stale monitor readback", async () =>
        {
            var f = new Fixture(100); using var cancellation = new CancellationTokenSource();
            f.Monitor.OnRead = () => { cancellation.Cancel(); return Task.CompletedTask; };
            await Throws<OperationCanceledException>(() => f.Engine.StartAsync(cancellation.Token));
            Equal(100, f.Audio.Percent); Equal(0, f.Audio.Writes.Count);
        }),
        ("An unconfirmed write preserves Windows volume and never settles the old reading", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Audio.UserSet(20); f.Monitor.ApplyWrites = false;
            await f.Tick(0); await f.Tick(20); await f.Tick(220); await f.Tick(420);
            await Throws<IOException>(() => f.Tick(620)); Equal(20, f.Audio.Percent);
            Equal(40, f.Engine.MonitorPercent); Equal(0, f.Audio.Writes.Count);
        }),
        ("Quantization changes Windows only after matching DDC confirmation", async () =>
        {
            var f = new Fixture(33, 4, 12); await f.Start(); f.Engine.SetPercent(55); f.Monitor.ApplyWrites = false;
            await f.Tick(20); await f.Tick(220); await f.Tick(420);
            Equal(55, f.Audio.Percent); Equal(33, f.Engine.MonitorPercent); Equal(1, f.Audio.Writes.Count);
            f.Monitor.Current = 7; await f.Tick(620);
            Equal(58, f.Audio.Percent); Equal(58, f.Engine.MonitorPercent); Equal(false, f.Engine.IsPending);
        }),
        ("Failed readback after a successful set preserves the Windows request", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Audio.UserSet(25);
            await f.Tick(0); await f.Tick(20); Equal(25u, f.Monitor.Current);
            f.Monitor.OnRead = () => throw new IOException("Readback failed");
            await Throws<IOException>(() => f.Tick(220));
            Equal(25, f.Audio.Percent); Equal(0, f.Audio.Writes.Count);
        }),
        ("A failed write never rolls Windows back", async () =>
        {
            var f = new Fixture(20); await f.Start();
            f.Monitor.OnWrite = () => throw new IOException("I2C");
            await Throws<IOException>(() => f.Tick(20)); Equal(20, f.Audio.Percent); Equal(0, f.Audio.Writes.Count);
        }),
        ("Native Windows volume changes include both zero and maximum", async () =>
        {
            foreach (var target in new[] { 0, 100 })
            {
                var f = new Fixture(); await f.Start(); f.Audio.UserSet(target);
                await f.Tick(0); await f.Tick(19); Equal(0, f.Monitor.Writes.Count);
                await f.Tick(20); await f.Tick(220);
                Equal(target, f.Engine.MonitorPercent); Equal(target, f.Audio.Percent); Equal(0, f.Audio.Writes.Count);
            }
        }),
        ("Continuous native key input coalesces without postponing writes", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Audio.UserSet(42); await f.Tick(0);
            for (var t = 5; t <= 20; t += 5) { f.Audio.UserSet(f.Audio.Percent + 2); await f.Tick(t); }
            Equal(1, f.Monitor.Writes.Count); Equal(50u, f.Monitor.Current);
            for (var t = 25; t <= 45; t += 5) { f.Audio.UserSet(f.Audio.Percent + 2); await f.Tick(t); }
            Equal(2, f.Monitor.Writes.Count); Equal(60u, f.Monitor.Current);
            await f.Tick(245); Equal(60, f.Engine.MonitorPercent); Equal(false, f.Engine.IsPending);
            Equal(0, f.Audio.Writes.Count);
        }),
        ("Native input during a delayed write supersedes its completion", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Audio.UserSet(42); await f.Tick(0);
            f.Monitor.OnWrite = () => { f.Audio.UserSet(44); return Task.CompletedTask; };
            await f.Tick(20); f.Monitor.OnWrite = null; await f.Tick(40); await f.Tick(240);
            Equal(44, f.Engine.MonitorPercent); Equal(44, f.Audio.Percent); Equal(0, f.Audio.Writes.Count);
        }),
        ("A newer Windows request during confirmation wins", async () =>
        {
            var f = new Fixture(20); await f.Start(); await f.Tick(20);
            f.Monitor.OnRead = () => { f.Audio.UserSet(15); return Task.CompletedTask; };
            await f.Tick(220); f.Monitor.OnRead = null; Equal(15, f.Audio.Percent);
            await f.Tick(240); await f.Tick(440); Equal(15, f.Engine.MonitorPercent); Equal(15, f.Audio.Percent);
        }),
        ("Hardware knob changes update Windows without a DDC echo", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Monitor.Current = 22; await f.Tick(5000);
            Equal(22, f.Engine.MonitorPercent); Equal(22, f.Audio.Percent); Equal(1, f.Audio.Writes.Count);
            await f.Tick(10000); Equal(0, f.Monitor.Writes.Count); Equal(1, f.Audio.Writes.Count);
        }),
        ("Windows and tray changes during a poll win over monitor readback", async () =>
        {
            foreach (var tray in new[] { false, true })
            {
                var f = new Fixture(); await f.Start(); f.Monitor.Current = 22;
                f.Monitor.OnRead = () =>
                {
                    if (tray) f.Engine.SetPercent(15); else f.Audio.UserSet(15);
                    return Task.CompletedTask;
                };
                await f.Tick(5000); f.Monitor.OnRead = null;
                Equal(40, f.Engine.MonitorPercent); Equal(15, f.Audio.Percent);
                await f.Tick(5020); await f.Tick(5220); Equal(15, f.Engine.MonitorPercent);
            }
        }),
        ("A change immediately before hardware mirroring is preserved", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Monitor.Current = 22;
            f.Audio.BeforeSet = () => f.Audio.UserSet(10); await f.Tick(5000); f.Audio.BeforeSet = null;
            Equal(10, f.Audio.Percent); Equal(0, f.Audio.Writes.Count);
            await f.Tick(5020); await f.Tick(5220); Equal(10, f.Engine.MonitorPercent); Equal(10, f.Audio.Percent);
        }),
        ("A newer change immediately before a tray set wins", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Audio.BeforeSet = () => f.Audio.UserSet(10);
            f.Engine.SetPercent(20); f.Audio.BeforeSet = null;
            await f.Tick(20); await f.Tick(220);
            Equal(10, f.Audio.Percent); Equal(10, f.Engine.MonitorPercent); Equal(0, f.Audio.Writes.Count);
        }),
        ("A change immediately after our Windows set remains authoritative", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Audio.AfterSet = () => f.Audio.UserSet(10);
            f.Engine.SetPercent(20); f.Audio.AfterSet = null;
            await f.Tick(20); await f.Tick(220);
            Equal(10, f.Audio.Percent); Equal(10, f.Engine.MonitorPercent);
        }),
        ("Delayed callback revisions do not echo a mirrored hardware level", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Monitor.Current = 22;
            f.Audio.AfterSet = () => f.Audio.Revision++; await f.Tick(5000);
            await f.Tick(5020); await f.Tick(10000);
            Equal(22, f.Engine.MonitorPercent); Equal(22, f.Audio.Percent);
            Equal(false, f.Engine.IsPending); Equal(0, f.Monitor.Writes.Count); Equal(1, f.Audio.Writes.Count);
        }),
        ("Mute is preserved across adoption, tray input, and hardware polling", async () =>
        {
            var f = new Fixture(100); f.Audio.Muted = true; await f.Start();
            f.Engine.SetPercent(20); await f.Tick(20); await f.Tick(220);
            f.Monitor.Current = 10; await f.Tick(6000);
            Equal(true, f.Audio.Muted); Equal(true, f.Engine.Muted); Equal(10, f.Audio.Percent);
        }),
        ("Mute-only notifications do not queue monitor volume writes", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Audio.Muted = true; f.Audio.Revision++;
            await f.Tick(20); await f.Tick(5000);
            Equal(0, f.Monitor.Writes.Count); Equal(0, f.Audio.Writes.Count); Equal(true, f.Engine.Muted);
        }),
        ("A mute change during a hardware poll invalidates that snapshot", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Monitor.Current = 22;
            f.Monitor.OnRead = () => { f.Audio.Muted = true; f.Audio.Revision++; return Task.CompletedTask; };
            await f.Tick(5000); f.Monitor.OnRead = null;
            Equal(40, f.Audio.Percent); Equal(0, f.Audio.Writes.Count);
            await f.Tick(10000); Equal(22, f.Audio.Percent); Equal(true, f.Audio.Muted);
        }),
        ("An inactive audio output is rejected before any DDC read", async () =>
        {
            var f = new Fixture(); f.Audio.Active = false;
            await Throws<IOException>(f.Start); Equal(0, f.Monitor.Reads);
        }),
        ("An output switch during discovery cannot change the old endpoint", async () =>
        {
            var f = new Fixture(100);
            f.Monitor.OnRead = () => { f.Audio.Active = false; return Task.CompletedTask; };
            await Throws<IOException>(f.Start); Equal(100, f.Audio.Percent); Equal(0, f.Audio.Writes.Count);
        }),
        ("An output switch before a queued write prevents the write", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Engine.SetPercent(20); f.Audio.Active = false;
            await Throws<IOException>(() => f.Tick(20)); Equal(0, f.Monitor.Writes.Count);
        }),
        ("An output switch during confirmation prevents Windows quantization", async () =>
        {
            var f = new Fixture(50, 6, 12); await f.Start(); f.Engine.SetPercent(52); await f.Tick(20);
            f.Monitor.OnRead = () => { f.Audio.Active = false; return Task.CompletedTask; };
            await Throws<IOException>(() => f.Tick(220)); Equal(52, f.Audio.Percent);
        }),
        ("Cancellation during confirmation cannot update Windows", async () =>
        {
            var f = new Fixture(50, 6, 12); await f.Start(); f.Engine.SetPercent(52); await f.Tick(20);
            using var cancellation = new CancellationTokenSource(); f.Now = 220;
            f.Monitor.OnRead = () => { cancellation.Cancel(); return Task.CompletedTask; };
            await Throws<OperationCanceledException>(() => f.Engine.TickAsync(cancellation.Token)); Equal(52, f.Audio.Percent);
        }),
        ("Changed monitor ranges invalidate control without updating Windows", async () =>
        {
            var f = new Fixture(); await f.Start(); f.Monitor.Maximum = 200;
            await Throws<IOException>(() => f.Tick(5000)); Equal(40, f.Audio.Percent); Equal(0, f.Audio.Writes.Count);
        }),
        ("Confirmed quantization aligns both levels without a feedback loop", async () =>
        {
            var f = new Fixture(50, 6, 12); await f.Start(); f.Engine.SetPercent(52);
            await f.Tick(20); Equal(52, f.Audio.Percent); await f.Tick(220);
            Equal(50, f.Engine.MonitorPercent); Equal(50, f.Audio.Percent);
            await f.Tick(240); await f.Tick(6000);
            Equal(1, f.Monitor.Writes.Count); Equal(2, f.Audio.Writes.Count); Equal(false, f.Engine.IsPending);
        }),
        ("New Windows input just before quantization correction wins", async () =>
        {
            var f = new Fixture(50, 6, 12); await f.Start(); f.Engine.SetPercent(52); await f.Tick(20);
            f.Audio.BeforeSet = () => f.Audio.UserSet(75); await f.Tick(220); f.Audio.BeforeSet = null;
            Equal(75, f.Audio.Percent); await f.Tick(240); await f.Tick(440);
            Equal(75, f.Engine.MonitorPercent); Equal(75, f.Audio.Percent);
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

    private sealed class Fixture(int percent = 40, uint raw = 40, uint maximum = 100)
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
