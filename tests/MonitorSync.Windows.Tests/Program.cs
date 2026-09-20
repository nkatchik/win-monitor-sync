using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using MonitorSync.App;
using MonitorSync.Core;
using MonitorSync.Windows;

static class Program
{
    [STAThread]
    static int Main()
    {
        // Run the real coordinator on its normal single dispatcher, without an app,
        // window, registry writes, worker process, or hardware access.
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var failures = 0;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            foreach (var (name, run) in Cases)
            {
                try { await run(); Console.WriteLine("PASS " + name); }
                catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
            }
            Console.WriteLine($"{Cases.Length - failures}/{Cases.Length} Windows controller tests passed.");
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
        }));
        Dispatcher.Run();
        return failures == 0 ? 0 : 1;
    }

    static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Same-output DDC recovery preserves an undelivered upward Windows request", () => Run(async (env, controller) =>
        {
            await Ready(controller);
            env.WriteFailures = 1; env.UserSet(80);
            await Until(() => env.FailedWrites == 1 && !controller.CanControlVolume);
            Equal(80, env.Percent); Equal(40u, env.Raw);
            await Settled(controller, 80);
            Equal(80, env.Percent); Equal(80u, env.Raw); Equal(0, env.AudioWrites.Count);
        })),
        ("Newer Windows input during recovery supersedes the failed request", () => Run(async (env, controller) =>
        {
            await Ready(controller);
            env.WriteFailures = 1; env.UserSet(80);
            await Until(() => env.FailedWrites == 1 && !controller.CanControlVolume);
            env.UserSet(60);
            await Settled(controller, 60);
            Equal(60, env.Percent); Equal(60u, env.Raw); Equal(0, env.AudioWrites.Count);
        })),
        ("Repeated write and discovery failures retain Windows volume and mute", () => Run(async (env, controller) =>
        {
            await Ready(controller);
            env.WriteFailures = 2; env.Muted = true; env.UserSet(90);
            await Until(() => env.FailedWrites == 1 && !controller.CanControlVolume);
            env.DiscoveryFailures = 1;
            await Settled(controller, 90);
            Equal(2, env.FailedWrites); Equal(90, env.Percent); Equal(true, env.Muted);
            Equal(0, env.AudioWrites.Count);
        })),
        ("A readback failure after delivery recovers without another monitor write", () => Run(async (env, controller) =>
        {
            await Ready(controller);
            env.FailReadAfterWrite = true; env.UserSet(80);
            await Until(() => env.FailedReads == 1 && !controller.CanControlVolume);
            await Settled(controller, 80);
            Equal(80, env.Percent); Equal(1, env.DeliveredWrites); Equal(0, env.AudioWrites.Count);
        })),
        ("Switching outputs away and back during retry restores initial lower alignment", () => Run(async (env, controller) =>
        {
            await Ready(controller);
            env.WriteFailures = 1; env.UserSet(80);
            await Until(() => env.FailedWrites == 1 && !controller.CanControlVolume);
            // Like Core Audio's permanent route invalidation, even if the final ID matches.
            foreach (var audio in env.Connections) audio.Invalidated = true;
            await Settled(controller, 40);
            Equal(40, env.Percent); Equal(40u, env.Raw); Equal(1, env.AudioWrites.Count);
        })),
        ("A different physical monitor is not treated as transport recovery", () => Run(async (env, controller) =>
        {
            await Ready(controller);
            env.WriteFailures = 1; env.UserSet(80);
            await Until(() => env.FailedWrites == 1 && !controller.CanControlVolume);
            env.MonitorId = "replacement-monitor"; env.Raw = 25;
            await Settled(controller, 25);
            Equal(25, env.Percent); Equal(25u, env.Raw); Equal(0, env.DeliveredWrites);
        })),
        ("Suspension invalidates recovery and resume uses fresh lower alignment", () => Run(async (env, controller) =>
        {
            await Ready(controller);
            env.WriteFailures = 1; env.UserSet(80);
            await Until(() => env.FailedWrites == 1 && !controller.CanControlVolume);
            controller.SetSuspended(true);
            await Until(() => env.Connections.All(audio => audio.Disposed));
            controller.SetSuspended(false);
            await Settled(controller, 40);
            Equal(40, env.Percent); Equal(0, env.DeliveredWrites);
        })),
        ("Failure before initial alignment does not incorrectly enable recovery policy", () => Run(async (env, controller) =>
        {
            await Settled(controller, 40);
            Equal(1, env.FailedReads); Equal(40, env.Percent); Equal(1, env.AudioWrites.Count);
        }, env => { env.Percent = 80; env.ReadFailures = 1; })),
        ("Incomplete discovery prevents connection and volume writes until resolved", () => Run(async (env, controller) =>
        {
            await Until(() => env.Discoveries > 0);
            Equal(false, controller.CanControlVolume); Equal(0, env.Connections.Count);
            Equal(0, env.DeliveredWrites); Equal(0, env.AudioWrites.Count);
            env.UnresolvedDisplay = false;
            await Ready(controller);
            Equal(40, env.Percent);
        }, env => env.UnresolvedDisplay = true)),
        ("Closing during recovery releases the retained audio subscription and stops retries", () => Run(async (env, controller) =>
        {
            await Ready(controller);
            env.WriteFailures = 1; env.UserSet(80);
            await Until(() => env.FailedWrites == 1 && !controller.CanControlVolume);
            await controller.CloseAsync();
            var discoveries = env.Discoveries;
            await Task.Delay(60);
            Equal(discoveries, env.Discoveries); Equal(80, env.Percent);
            Equal(true, env.Connections.All(audio => audio.Disposed));
        }))
    ];

    static async Task Run(Func<FakeEnvironment, SyncController, Task> test, Action<FakeEnvironment>? configure = null)
    {
        var env = new FakeEnvironment(); configure?.Invoke(env);
        using var controller = new SyncController(env);
        controller.Start();
        try { await test(env, controller); }
        finally { await controller.CloseAsync(); }
        Equal(true, env.Connections.All(audio => audio.Disposed));
    }

    static Task Ready(SyncController controller) => Until(() => controller.CanControlVolume);
    static Task Settled(SyncController controller, int percent) =>
        Until(() => controller.CanControlVolume && !controller.IsPending && controller.VolumePercent == percent);

    static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Controller did not reach the expected state.");
            await Task.Delay(10);
        }
    }

    static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}.");
    }
}

sealed class FakeEnvironment : IVolumeEnvironment, IMonitorVolume
{
    public int Percent = 40;
    public uint Raw = 40;
    public bool Muted, FailReadAfterWrite, UnresolvedDisplay;
    public string MonitorId = "monitor";
    public long Revision;
    public int WriteFailures, ReadFailures, DiscoveryFailures, FailedWrites, FailedReads, DeliveredWrites, Discoveries;
    public List<int> AudioWrites { get; } = [];
    public List<FakeAudio> Connections { get; } = [];
    public void UserSet(int percent) { Percent = percent; Revision++; }
    public AudioDeviceInfo DescribeDefault() => new("output", "Test monitor", Percent, Muted, 0, "Test monitor", true);
    public IAudioConnection OpenAudio(string endpointId)
    {
        var connection = new FakeAudio(this, endpointId);
        Connections.Add(connection);
        return connection;
    }
    public Task<MonitorDescriptor[]> ListAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Discoveries++;
        if (DiscoveryFailures > 0) { DiscoveryFailures--; throw new IOException("Simulated discovery failure."); }
        List<MonitorDescriptor> monitors = [new(MonitorId, "Test monitor", new(Raw, 100), null, null)];
        if (UnresolvedDisplay) monitors.Add(new("unknown", "Generic PnP Monitor", null, null, "Open failed", false));
        return Task.FromResult(monitors.ToArray());
    }
    public IMonitorVolume OpenMonitor(string monitorId, string endpointId) => this;
    public void Log(string message) { }
    public Task<VolumeReading> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ReadFailures > 0) { ReadFailures--; FailedReads++; throw new IOException("Simulated read failure."); }
        return Task.FromResult(new VolumeReading(Raw, 100));
    }
    public Task WriteAsync(uint rawValue, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (WriteFailures > 0) { WriteFailures--; FailedWrites++; throw new IOException("Simulated failure before delivery."); }
        Raw = rawValue; DeliveredWrites++;
        if (FailReadAfterWrite) { FailReadAfterWrite = false; ReadFailures++; }
        return Task.CompletedTask;
    }
}

sealed class FakeAudio(FakeEnvironment env, string endpointId) : IAudioConnection
{
    public bool Disposed, Invalidated;
    public bool IsCurrentRoute => !Disposed && !Invalidated;
    public AudioSnapshot Capture()
    {
        if (!IsCurrentRoute) throw new AudioRouteChangedException("Simulated output change.");
        return new(endpointId, env.Percent, env.Muted, env.Revision);
    }
    public bool TrySetPercent(int percent, AudioSnapshot expected)
    {
        if (Capture() != expected) return false;
        env.Percent = percent; env.AudioWrites.Add(percent);
        return true;
    }
    public void Dispose() => Disposed = true;
}
