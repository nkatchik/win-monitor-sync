using System.Diagnostics;
using MonitorSync.Core;
using MonitorSync.Windows;

namespace MonitorSync.App;

public sealed class BrightnessController(DdcClient client) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Session? _session;
    private Task _pump = Task.CompletedTask;
    private BrightnessFlyout? _flyout;
    private bool _suspended, _disposed;

    public void Adjust(int delta)
    {
        if (_disposed || _suspended) return;
        var target = CursorDisplay.Capture();
        if (target is null) { Invalidate(); return; }
        if (_session is not { } session || session.Target.Handle != target.Handle || session.Target.Id != target.Id)
        {
            session = new Session(target);
            _session = session;
            session.Engine = new BrightnessAdjuster(new CursorBrightness(client, target.Id),
                () => ReferenceEquals(_session, session) && target.IsCurrent(), () => _clock.ElapsedMilliseconds);
        }
        session.Engine!.Step(delta);
        _flyout ??= new BrightnessFlyout();
        if (session.Engine.HasReading) _flyout.ShowLevel(target, session.Engine.Percent, session.Engine.IsPending);
        else _flyout.ShowMessage(target, "Brightness");
        if (_pump.IsCompleted) _pump = PumpAsync(_lifetime.Token);
    }

    private async Task PumpAsync(CancellationToken token)
    {
        while (_session is { } session && !token.IsCancellationRequested)
        {
            try
            {
                var engine = session.Engine!;
                await engine.StartAsync(token);
                _flyout?.ShowLevel(session.Target, engine.Percent, engine.IsPending);
                var shown = (engine.Percent, engine.IsPending);
                while (engine.IsPending)
                {
                    await engine.TickAsync(token);
                    var current = (engine.Percent, engine.IsPending);
                    if (current != shown) { _flyout?.ShowLevel(session.Target, current.Percent, current.IsPending); shown = current; }
                    if (engine.IsPending) await Task.Delay(25, token);
                }
            }
            catch (MonitorTargetChangedException) { _flyout?.HideImmediately(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                SettingsStore.Log(error.ToString());
                if (ReferenceEquals(_session, session)) _flyout?.ShowMessage(session.Target, "Brightness unavailable");
            }
            finally { if (ReferenceEquals(_session, session)) _session = null; }
        }
    }

    public void Invalidate()
    {
        _session = null;
        _flyout?.HideImmediately();
    }

    public void SetSuspended(bool suspended) { _suspended = suspended; Invalidate(); }

    public async Task CloseAsync()
    {
        if (_disposed) return;
        _lifetime.Cancel();
        Invalidate();
        await _pump;
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _session = null;
        _flyout?.Close();
        _lifetime.Dispose();
    }

    private sealed class Session(CursorDisplay target)
    {
        public CursorDisplay Target { get; } = target;
        public BrightnessAdjuster? Engine;
    }

    private sealed class CursorBrightness(DdcClient ddc, string id) : IMonitorVolume
    {
        public Task<VolumeReading> ReadAsync(CancellationToken token) => ddc.ReadCursorBrightnessAsync(id, token);
        public Task WriteAsync(uint value, CancellationToken token) => ddc.WriteCursorBrightnessAsync(id, value, token);
    }
}
