using System.Text.Json;
using MonitorSync.Core;

static class AuditRegressionTests
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("An unresolved display prevents another display from appearing uniquely matched", () =>
        {
            var working = new MonitorDescriptor("first", "DELL S2725QS", new(40, 100), null, null);
            foreach (var name in new[] { "DELL S2725QS", "Generic PnP Monitor", "Unknown display", "" })
            {
                var unresolved = new MonitorDescriptor("second", name, null, null, "Discovery failed", IdentityKnown: false);
                Equal<MonitorDescriptor?>(null, AutomaticMonitorSelection.Find("DELL S2725QS", true, [working, unresolved]));
                Equal<MonitorDescriptor?>(null, AutomaticMonitorSelection.Find("DELL S2725QS", true, [unresolved, working]));
            }
            return Task.CompletedTask;
        }),
        ("A blank monitor identity cannot establish uniqueness", () =>
        {
            var working = new MonitorDescriptor("first", "DELL S2725QS", new(40, 100), null, null);
            var unnamed = new MonitorDescriptor("second", " ", null, null, null);
            Equal<MonitorDescriptor?>(null, AutomaticMonitorSelection.Find("DELL S2725QS", true, [working, unnamed]));
            return Task.CompletedTask;
        }),
        ("A known unrelated display without DDC volume does not prevent a safe match", () =>
        {
            var working = new MonitorDescriptor("first", "DELL S2725QS", new(40, 100), null, null);
            var unrelated = new MonitorDescriptor("second", "LG 27UP850", null, new(60, 100), "No volume support");
            Equal(working, AutomaticMonitorSelection.Find("DELL S2725QS", true, [working, unrelated]));
            return Task.CompletedTask;
        }),
        ("Unresolved identities survive the worker protocol and block pairing until rediscovery", () =>
        {
            var working = new MonitorDescriptor("first", "DELL S2725QS", new(40, 100), null, null);
            var unknown = new MonitorDescriptor("second", "Generic PnP Monitor", null, null, "Open failed", false);
            var reply = JsonSerializer.Deserialize<WorkerResponse>(JsonSerializer.Serialize(new WorkerResponse(true, Monitors: [working, unknown])))!;
            Equal<MonitorDescriptor?>(null, AutomaticMonitorSelection.Find("DELL S2725QS", true, reply.Monitors!));
            var resolved = unknown with { Name = "LG 27UP850", IdentityKnown = true };
            Equal(working, AutomaticMonitorSelection.Find("DELL S2725QS", true, [working, resolved]));
            return Task.CompletedTask;
        }),
        ("Separate brightness-down presses each advance a coarse monitor", async () =>
        {
            var monitor = new FakeMonitor(4, 10);
            for (uint expected = 3; expected > 0; expected--)
            {
                long now = 0;
                var engine = new BrightnessAdjuster(monitor, () => true, () => now);
                engine.Step(-5);
                await engine.StartAsync(default);
                now = 20; await engine.TickAsync(default);
                now = 220; await engine.TickAsync(default);
                Equal(expected, monitor.Current);
                Equal(false, engine.IsPending);
            }
            Equal(3, monitor.Writes.Count);
        }),
        ("Brightness keys make progress in both directions throughout coarse ranges", async () =>
        {
            for (uint maximum = 1; maximum <= 20; maximum++)
            for (uint raw = 0; raw <= maximum; raw++)
            foreach (var delta in new[] { -5, 5 })
            foreach (var duringDiscovery in new[] { true, false })
            {
                var f = new BrightnessFixture(raw, maximum);
                if (duringDiscovery) f.Engine.Step(delta);
                await f.Start();
                if (!duringDiscovery) f.Engine.Step(delta);
                await f.Tick(20); await f.Tick(220);
                if (delta < 0 && raw > 0) Equal(true, f.Monitor.Current < raw);
                else if (delta > 0 && raw < maximum) Equal(true, f.Monitor.Current > raw);
                else { Equal(raw, f.Monitor.Current); Equal(0, f.Monitor.Writes.Count); }
                Equal(false, f.Engine.IsPending);
            }
        }),
        ("Coarse brightness keys preserve slider and reversal order during discovery", async () =>
        {
            var f = new BrightnessFixture(4, 10);
            f.Engine.SetPercent(70); f.Engine.Step(-5); f.Engine.Step(5);
            f.Engine.SetPercent(20); f.Engine.Step(-5);
            await f.Start(); await f.Tick(20); await f.Tick(220);
            Equal(1u, f.Monitor.Current); Equal(10, f.Engine.Percent);
            Equal(1, f.Monitor.Writes.Count);
        }),
        ("Coarse brightness key input supersedes an in-flight slider write", async () =>
        {
            var f = new BrightnessFixture(4, 10); await f.Start(); f.Engine.SetPercent(80);
            f.Monitor.OnWrite = () => { f.Engine.Step(-5); return Task.CompletedTask; };
            await f.Tick(20); f.Monitor.OnWrite = null;
            Equal(true, f.Engine.IsPending);
            await f.Tick(40); await f.Tick(240);
            Equal(7u, f.Monitor.Current); Equal(70, f.Engine.Percent); Equal(false, f.Engine.IsPending);
        })
    ];

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}; got {actual}.");
    }
}
