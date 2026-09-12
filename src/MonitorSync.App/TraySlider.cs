using System.Drawing;
using Forms = System.Windows.Forms;

namespace MonitorSync.App;

/// <summary>A native, keyboard-accessible slider hosted directly in the tray menu.</summary>
public sealed class TraySlider : Forms.ToolStripControlHost
{
    private readonly SliderSurface _surface;
    private readonly string _name;
    private bool _updating;
    private bool _hasValue;
    public event Action<int>? ValueRequested;

    public TraySlider(string name, int smallChange) : base(new SliderSurface(name, smallChange))
    {
        _name = name;
        _surface = (SliderSurface)Control;
        AutoSize = false;
        Size = _surface.Size;
        Margin = new Forms.Padding(0, 2, 0, 2);
        AccessibleName = name;
        Enabled = false;
        _surface.SizeChanged += (_, _) => Size = _surface.Size;
        _surface.Slider.ValueChanged += (_, _) =>
        {
            if (_updating || !Enabled) return;
            _surface.Caption.Text = $"{_name} {_surface.Slider.Value}%";
            ValueRequested?.Invoke(_surface.Slider.Value);
        };
    }

    public void UpdateLevel(int? percent, bool pending)
    {
        Enabled = percent.HasValue;
        if (percent is not int confirmed)
        {
            _hasValue = false;
            _surface.Slider.Capture = false;
            _surface.Caption.Text = _name;
            return;
        }
        if ((_hasValue && pending) || _surface.Slider.Capture) return;
        _updating = true;
        try
        {
            _surface.Slider.Value = Math.Clamp(confirmed, 0, 100);
            _surface.Caption.Text = $"{_name} {confirmed}%";
            _hasValue = true;
        }
        finally { _updating = false; }
    }

    public void MatchMenu(Forms.ContextMenuStrip menu)
    {
        var background = menu.Renderer is Forms.ToolStripProfessionalRenderer renderer
            ? renderer.ColorTable.ToolStripDropDownBackground : menu.BackColor;
        _surface.BackColor = _surface.Slider.BackColor = background;
        _surface.Caption.ForeColor = menu.ForeColor;
    }

    private sealed class SliderSurface : Forms.UserControl
    {
        public Forms.Label Caption { get; }
        public Forms.TrackBar Slider { get; }

        public SliderSurface(string name, int smallChange)
        {
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = Forms.AutoScaleMode.Dpi;
            Size = new Size(260, 66);
            Padding = new Forms.Padding(8, 4, 8, 0);
            BackColor = SystemColors.Menu;
            Caption = new Forms.Label
            {
                Text = name, Dock = Forms.DockStyle.Top, Height = 20,
                ForeColor = SystemColors.MenuText, TextAlign = ContentAlignment.MiddleLeft
            };
            Slider = new Forms.TrackBar
            {
                Minimum = 0, Maximum = 100, SmallChange = smallChange, LargeChange = 10,
                TickStyle = Forms.TickStyle.None, Dock = Forms.DockStyle.Fill, AutoSize = false,
                AccessibleName = name, BackColor = SystemColors.Menu, TabStop = true
            };
            Controls.Add(Slider);
            Controls.Add(Caption);
        }
    }
}
