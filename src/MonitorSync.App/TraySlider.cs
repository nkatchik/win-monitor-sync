using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace MonitorSync.App;

/// <summary>A standard WPF slider using the popup's Fluent theme.</summary>
public sealed class TraySlider : UserControl
{
    private readonly TextBlock _value = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _caption;
    private bool _updating, _hasValue;
    public Slider Slider { get; }
    public CheckBox Active { get; }
    public event Action<int>? ValueRequested;

    public TraySlider(string name, int smallChange)
    {
        Focusable = false;
        IsTabStop = false;
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        var panel = new StackPanel();
        var header = new Grid();
        _caption = new TextBlock { Text = name };
        Active = new CheckBox { Content = _caption, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(Active, $"{name} active");
        header.Children.Add(Active);
        _value.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(_value);
        Slider = new Slider
        {
            Minimum = 0, Maximum = 100, SmallChange = smallChange, LargeChange = 10,
            TickFrequency = 1, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true,
            Margin = new Thickness(0, 4, 0, 0), Opacity = 0.4, IsEnabled = false
        };
        AutomationProperties.SetName(Slider, name);
        AutomationProperties.SetLabeledBy(Slider, _caption);
        panel.Children.Add(header);
        panel.Children.Add(Slider);
        Content = panel;
        Slider.ValueChanged += (_, _) =>
        {
            if (_updating || !Slider.IsEnabled) return;
            var percent = (int)Math.Round(Slider.Value);
            _value.Text = $"{percent}%";
            ValueRequested?.Invoke(percent);
        };
        UpdateLevel(null, false);
    }

    public void UpdateLevel(int? percent, bool pending)
    {
        // Availability disables only the level control. Its Active checkbox is always usable.
        if (!percent.HasValue && Slider.IsKeyboardFocusWithin) Active.Focus();
        Slider.IsEnabled = percent.HasValue;
        _caption.SetResourceReference(TextBlock.ForegroundProperty,
            Slider.IsEnabled ? "TextFillColorPrimaryBrush" : "TextFillColorDisabledBrush");
        // The framework's Fluent slider template retains its accent even when disabled.
        Slider.Opacity = Slider.IsEnabled ? 1 : 0.4;
        if (percent is not int confirmed)
        {
            _hasValue = false;
            if (Slider.IsMouseCaptureWithin) Mouse.Capture(null);
            _value.Text = "";
            return;
        }
        if ((_hasValue && pending) || Slider.IsMouseCaptureWithin || Slider.IsStylusCaptureWithin) return;
        _updating = true;
        try
        {
            Slider.Value = Math.Clamp(confirmed, 0, 100);
            _value.Text = $"{confirmed}%";
            _hasValue = true;
        }
        finally { _updating = false; }
    }
}
