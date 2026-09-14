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
    private Task _refreshTask = Task.CompletedTask;
    private (CursorDisplay Target, int Percent)? _reading;
    private long _revision;
    private bool _suspended, _disposed;

    public event EventHandler? Changed;
    public int? Percent => !_suspended && !_disposed && _reading is { } reading && reading.Target.IsCurrent()
        ? reading.Percent : null;
    public bool IsPending => _session is not null;

    // Tray discovery is read-only and never replaces a newer key/slider request.
    public Task RefreshAsync()
    {
        if (_disposed || _suspended || _session is not null || !_refreshTask.IsCompleted) return _refreshTask;
        return _refreshTask = RefreshCoreAsync();
    }

    private async Task RefreshCoreAsync()
    {
        if (_disposed || _suspended || _session is not null) return;
        var target = CursorDisplay.Capture();
        if (target is null) { Invalidate(); return; }
        var revision = _revision;
        try
        {
            var reading = await client.ReadCursorBrightnessAsync(target.Id, _lifetime.Token);
            reading.Validate();
            if (_disposed || _suspended || revision != _revision || !target.IsCurrent()) return;
            _reading = (target, reading.Percent);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (MonitorTargetChangedException) { }
        catch (Exception error)
        {
            SettingsStore.Log(error.ToString());
            if (revision == _revision)
            {
                _reading = null;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool SetPercent(int percent)
    {
        if (Percent is null || _reading is not { } reading) return false;
        Queue(reading.Target, engine => engine.SetPercent(percent));
        return true;
    }

    public void Adjust(int delta)
    {
        if (_disposed || _suspended) return;
        var target = CursorDisplay.Capture();
        if (target is null) { Invalidate(); return; }
        Queue(target, engine => engine.Step(delta));
    }

    private void Queue(CursorDisplay target, Action<BrightnessAdjuster> input)
    {
        _revision++;
        if (_session is not { } session || session.Target.Handle != target.Handle || session.Target.Id != target.Id)
        {
            session = new Session(target);
            _session = session;
            session.Engine = new BrightnessAdjuster(new CursorBrightness(client, target.Id),
                () => ReferenceEquals(_session, session) && target.IsCurrent(), () => _clock.ElapsedMilliseconds);
        }
        input(session.Engine!);
        Changed?.Invoke(this, EventArgs.Empty);
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
                Publish(session);
                var published = (engine.Percent, engine.IsPending);
                while (engine.IsPending)
                {
                    await engine.TickAsync(token);
                    var current = (engine.Percent, engine.IsPending);
                    if (current != published) { Publish(session); published = current; }
                    if (engine.IsPending) await Task.Delay(20, token);
                }
            }
            catch (MonitorTargetChangedException) { }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                SettingsStore.Log(error.ToString());
                if (ReferenceEquals(_session, session))
                    _reading = null;
            }
            finally
            {
                if (ReferenceEquals(_session, session))
                {
                    _session = null;
                    _revision++;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    private void Publish(Session session)
    {
        if (!ReferenceEquals(_session, session) || !session.Target.IsCurrent()) return;
        _reading = (session.Target, session.Engine!.Percent);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Invalidate()
    {
        _session = null;
        _reading = null;
        _revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetSuspended(bool suspended) { _suspended = suspended; Invalidate(); }

    public async Task CloseAsync()
    {
        if (_disposed) return;
        _lifetime.Cancel();
        Invalidate();
        await Task.WhenAll(_pump, _refreshTask);
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _session = null;
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
