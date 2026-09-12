using System.Diagnostics;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace MonitorSync.App;

public sealed class TraySliders : IDisposable
{
    private readonly Forms.ContextMenuStrip _menu;
    private readonly SyncController _volume;
    private readonly BrightnessController _brightness;
    private readonly TraySlider _volumeSlider = new("Volume", 2);
    private readonly TraySlider _brightnessSlider = new("Brightness", 5);
    private readonly Forms.Timer _refresh = new() { Interval = 100 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _probing, _disposed;
    private long _probeDue;

    public TraySliders(Forms.ContextMenuStrip menu, SyncController volume, BrightnessController brightness)
    {
        _menu = menu;
        _volume = volume;
        _brightness = brightness;
        menu.Items.Insert(1, _volumeSlider);
        menu.Items.Insert(2, _brightnessSlider);
        _volumeSlider.ValueRequested += percent => { _volume.SetVolumePercent(percent); Update(); };
        _brightnessSlider.ValueRequested += percent => { _brightness.SetPercent(percent); Update(); };
        menu.Opened += Opened;
        menu.Closed += Closed;
        menu.Closing += Closing;
        volume.Changed += Changed;
        brightness.Changed += Changed;
        _refresh.Tick += Tick;
    }

    private void Opened(object? sender, EventArgs e)
    {
        _probeDue = 0;
        _volumeSlider.MatchMenu(_menu);
        _brightnessSlider.MatchMenu(_menu);
        Update();
        _refresh.Start();
        Tick(sender, e);
    }

    private void Closed(object? sender, Forms.ToolStripDropDownClosedEventArgs e) => _refresh.Stop();

    private void Closing(object? sender, Forms.ToolStripDropDownClosingEventArgs e)
    {
        // Interacting with a hosted slider must not dismiss the menu.
        if (e.CloseReason == Forms.ToolStripDropDownCloseReason.ItemClicked &&
            (_volumeSlider.Selected || _brightnessSlider.Selected)) e.Cancel = true;
    }

    private void Changed(object? sender, EventArgs e) { if (_menu.Visible) Update(); }

    private async void Tick(object? sender, EventArgs e)
    {
        if (_disposed || !_menu.Visible) return;
        Update();
        if (_probing || _clock.ElapsedMilliseconds < _probeDue) return;
        _probing = true;
        try { await _brightness.RefreshAsync(); }
        finally
        {
            _probing = false;
            _probeDue = _clock.ElapsedMilliseconds + 2000;
            if (!_disposed && _menu.Visible) Update();
        }
    }

    private void Update()
    {
        if (_disposed) return;
        _volumeSlider.UpdateLevel(_volume.VolumePercent, _volume.IsPending);
        _brightnessSlider.UpdateLevel(_brightness.Percent, _brightness.IsPending);
        if (!_menu.Visible) return;
        // As async discovery adds/removes rows, keep the popup in its current screen's work area.
        var area = Forms.Screen.FromControl(_menu).WorkingArea;
        var location = new Point(Math.Clamp(_menu.Left, area.Left, Math.Max(area.Left, area.Right - _menu.Width)),
            Math.Clamp(_menu.Top, area.Top, Math.Max(area.Top, area.Bottom - _menu.Height)));
        if (_menu.Location != location) _menu.Location = location;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refresh.Stop();
        _refresh.Dispose();
        _menu.Opened -= Opened;
        _menu.Closed -= Closed;
        _menu.Closing -= Closing;
        _volume.Changed -= Changed;
        _brightness.Changed -= Changed;
        // The menu owns and disposes the hosted controls.
    }
}
