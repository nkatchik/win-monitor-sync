using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MonitorSync.Core;

namespace MonitorSync.App;

public sealed class DdcClient : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private bool _initialized;

    public async Task<MonitorDescriptor[]> ListAsync(CancellationToken token = default) =>
        (await RequestAsync(new("list"), token)).Monitors ?? [];

    public async Task<VolumeReading> ReadAsync(string id, byte code, CancellationToken token = default) =>
        (await RequestAsync(new("read", id, code), token)).Reading
        ?? throw new IOException("The monitor worker returned no value.");

    public async Task WriteAsync(string id, byte code, uint value, CancellationToken token = default) =>
        _ = await RequestAsync(new("write", id, code, value), token);

    public async Task<VolumeReading> ReadVolumeAsync(string id, string endpointId, CancellationToken token) =>
        (await RequestAsync(new("read-volume", id, AudioEndpointId: endpointId), token)).Reading
        ?? throw new IOException("The monitor returned no volume value.");

    public async Task WriteVolumeAsync(string id, string endpointId, uint value, CancellationToken token) =>
        _ = await RequestAsync(new("write-volume", id, Value: value, AudioEndpointId: endpointId), token);

    public async Task<VolumeReading> ReadCursorBrightnessAsync(string id, CancellationToken token) =>
        (await RequestAsync(new("read-brightness", id), token)).Reading
        ?? throw new IOException("The monitor returned no brightness value.");

    public async Task WriteCursorBrightnessAsync(string id, uint value, CancellationToken token) =>
        _ = await RequestAsync(new("write-brightness", id, Value: value), token);

    private async Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_process is null || _process.HasExited)
            {
                Stop();
                var path = Path.Combine(AppContext.BaseDirectory, "MonitorSync.Worker.exe");
                if (!File.Exists(path)) throw new FileNotFoundException("MonitorSync.Worker.exe is missing. Repair the installation.", path);
                var info = new ProcessStartInfo(path)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                info.ArgumentList.Add("--parent");
                info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _process = Process.Start(info) ?? throw new IOException("Could not start the monitor worker.");
                _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) SettingsStore.Log(e.Data); };
                _process.BeginErrorReadLine();
            }
            if (!_initialized && request.Operation is ("read" or "write" or "read-volume" or "write-volume"))
            {
                await SendAsync(new("list"), token);
                _initialized = true;
            }
            var response = await SendAsync(request, token);
            if (request.Operation == "list") _initialized = true;
            return response;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Stop();
            throw new IOException("The monitor stopped responding. Sync will retry automatically.");
        }
        catch (OperationCanceledException) { Stop(); throw; }
        catch (IOException) { Stop(); throw; }
        finally { _gate.Release(); }
    }

    private async Task<WorkerResponse> SendAsync(WorkerRequest request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(request.Operation == "list" ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(3));
        var process = _process ?? throw new IOException("Monitor worker is not running.");
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token);
        await process.StandardInput.FlushAsync(deadline.Token);
        var line = await process.StandardOutput.ReadLineAsync(deadline.Token)
            ?? throw new IOException("The monitor worker exited unexpectedly.");
        WorkerResponse response;
        try { response = JsonSerializer.Deserialize<WorkerResponse>(line) ?? throw new JsonException(); }
        catch (JsonException e) { throw new IOException("Invalid monitor worker response.", e); }
        if (response.TargetChanged) throw new MonitorTargetChangedException();
        if (!response.Success) throw new IOException(response.Error ?? "Monitor operation failed.");
        return response;
    }

    private void Stop()
    {
        if (_process is { } process)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }
        _process = null;
        _initialized = false;
    }

    public void Dispose() => Stop();
}

internal sealed class MonitorVolume(DdcClient client, string id, string endpointId) : IMonitorVolume
{
    public Task<VolumeReading> ReadAsync(CancellationToken cancellationToken) => client.ReadVolumeAsync(id, endpointId, cancellationToken);
    public Task WriteAsync(uint rawValue, CancellationToken cancellationToken) => client.WriteVolumeAsync(id, endpointId, rawValue, cancellationToken);
}
