using System.Text.Json;
using MonitorSync.Core;
using MonitorSync.Windows;

if (args is ["--parent", var parentId] && int.TryParse(parentId, out var pid))
{
    // Even if the main thread is stuck inside a display driver, this task ends
    // the worker when its owner exits. There is no persistent background service.
    var parent = System.Diagnostics.Process.GetProcessById(pid);
    _ = Task.Run(async () => { using (parent) await parent.WaitForExitAsync(); Environment.Exit(0); });
}
using var monitors = new PhysicalMonitors();
while (Console.ReadLine() is { } line)
{
    WorkerResponse response;
    try
    {
        var request = JsonSerializer.Deserialize<WorkerRequest>(line)
            ?? throw new IOException("Empty request.");
        response = request.Operation switch
        {
            "list" => new(true, Monitors: monitors.Enumerate()),
            "read" => new(true, Reading: monitors.Read(request.MonitorId ?? "", request.Code)),
            "write" => Write(request),
            "read-brightness" => new(true, Reading: monitors.ReadCursorBrightness(request.MonitorId ?? "")),
            "write-brightness" => WriteBrightness(request),
            _ => new(false, "Unknown operation.")
        };
    }
    catch (Exception e) { response = new(false, e.Message, TargetChanged: e is MonitorTargetChangedException); }
    Console.WriteLine(JsonSerializer.Serialize(response));
}

WorkerResponse Write(WorkerRequest request)
{
    monitors.Write(request.MonitorId ?? "", request.Code, request.Value);
    return new(true);
}

WorkerResponse WriteBrightness(WorkerRequest request)
{
    monitors.WriteCursorBrightness(request.MonitorId ?? "", request.Value);
    return new(true);
}
