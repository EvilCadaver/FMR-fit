using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FmrFit.Core.Numbers;
using HAlign = Avalonia.Layout.HorizontalAlignment;

namespace FmrFit.App;

/// <summary>
/// Entry for one number, used for every numeric input so all windows behave alike:
/// <list type="bullet">
/// <item>Typing changes nothing until Enter or leaving the field; then the text is parsed (invariant culture, a comma is
/// accepted as the decimal point), clamped to <see cref="Minimum"/>…<see cref="Maximum"/> and, if the value changed,
/// <see cref="ValueCommitted"/> is raised. Invalid text is replaced by the current value. Esc restores the current value.</item>
/// <item>The mouse wheel, while the field has focus, steps the value and commits it at once: by <see cref="Step"/>, or
/// (Step = 0) by one unit of the value's second significant digit. Shift steps 10× larger, Ctrl 10× smaller.</item>
/// <item>Setting <see cref="Value"/> from code raises no event and leaves text the user is typing alone.</item>
/// </list>
/// </summary>
public class NumberBox : TextBox
{
    /// <summary>How the entries are used; shown in their tooltips.</summary>
    public const string UsageTip =
        "Applied on Enter or when leaving the field; Esc restores the value. Mouse wheel (while editing): one step, "
        + "Shift ×10, Ctrl ÷10.";

    public static readonly RoutedEvent<RoutedEventArgs> ValueCommittedEvent =
        RoutedEvent.Register<NumberBox, RoutedEventArgs>(nameof(ValueCommitted), RoutingStrategies.Direct);

    private double _value;
    private string? _format;
    private string? _description;

    /// <summary>Last non-zero value, the scale of relative wheel steps while the value is 0.</summary>
    private double _scale;

    /// <summary>Value after the last wheel step and the step exponent used, so a run of steps keeps one step size.</summary>
    private double _wheelValue = double.NaN;
    private int _wheelExponent;

    public NumberBox()
    {
        HorizontalContentAlignment = HAlign.Right;
        Text = Show(_value);
        LostFocus += (_, _) => Commit();
        // Tunnel: see Enter and Esc before the text box handles them.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        // The text box's own scroll viewer may mark the wheel handled; step anyway.
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Bubble, handledEventsToo: true);
        UpdateTip();
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>Raised after the user changed <see cref="Value"/> (Enter, leaving the field or the mouse wheel).</summary>
    public event EventHandler<RoutedEventArgs>? ValueCommitted
    {
        add => AddHandler(ValueCommittedEvent, value);
        remove => RemoveHandler(ValueCommittedEvent, value);
    }

    /// <summary>The value; setting it clamps it and shows it, unless the user is typing in the field.</summary>
    public double Value
    {
        get => _value;
        set
        {
            var editing = IsKeyboardFocusWithin && Text != Show(_value);
            Store(value);
            if (!editing)
                Text = Show(_value);
        }
    }

    public double Minimum { get; set; } = double.NegativeInfinity;

    public double Maximum { get; set; } = double.PositiveInfinity;

    /// <summary>Wheel step, a power of ten (e.g. 0.1); 0 = one unit of the value's second significant digit.</summary>
    public double Step { get; set; }

    /// <summary>.NET format of the shown value (e.g. "0.0", "0.####E+0"); null = shortest exact text.</summary>
    public string? Format
    {
        get => _format;
        set
        {
            _format = value;
            if (!IsKeyboardFocusWithin)
                Text = Show(_value);
        }
    }

    /// <summary>What the entry is; its tooltip is this followed by <see cref="UsageTip"/>.</summary>
    public string? Description
    {
        get => _description;
        set
        {
            _description = value;
            UpdateTip();
        }
    }

    /// <summary>Value used when the field is left empty; null = empty text is invalid.</summary>
    public Func<double>? EmptyValue { get; set; }

    private void UpdateTip() =>
        ToolTip.SetTip(this, string.IsNullOrEmpty(_description) ? UsageTip : _description + "\n" + UsageTip);

    private string Show(double value) => value.ToString(_format ?? "R", CultureInfo.InvariantCulture);

    private void Store(double value)
    {
        _value = double.IsFinite(value) ? Math.Clamp(value, Minimum, Maximum) : 0;
        if (_value != 0)
            _scale = _value;
    }

    private bool IsEdited => (Text?.Trim() ?? "") != Show(_value);

    /// <summary>Applies the typed text, if changed, and shows the resulting value.</summary>
    private void Commit()
    {
        if (IsEdited)
        {
            var text = Text?.Trim() ?? "";
            if ((text.Length == 0 ? EmptyValue?.Invoke() : Parse(text)) is { } typed)
                Change(typed);
        }
        Text = Show(_value);
    }

    /// <summary>Stores a value from the user and raises <see cref="ValueCommitted"/> if it differs from the current one.</summary>
    private void Change(double value)
    {
        var old = _value;
        Store(value);
        if (_value != old)
            RaiseEvent(new RoutedEventArgs(ValueCommittedEvent));
    }

    private static double? Parse(string text) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
        && double.IsFinite(v)
            ? v
            : null;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Commit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && IsEdited)
        {
            // Only when there is something to restore, so Esc otherwise still reaches the window (e.g. Cancel).
            Text = Show(_value);
            e.Handled = true;
        }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        // Without focus the wheel scrolls the panel as usual.
        if (!IsEnabled || IsReadOnly || !IsKeyboardFocusWithin)
            return;
        var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X; // Shift+wheel may arrive as horizontal scrolling
        e.Handled = true;
        if (delta == 0)
            return;

        // Step from what is typed, if valid, so typing and then scrolling continues from the typed number.
        var start = IsEdited && Parse(Text!.Trim()) is { } typed ? Math.Clamp(typed, Minimum, Maximum) : _value;
        int? exponent = Step > 0
            ? WheelStep.ExponentOf(Step)
            : start == _wheelValue ? _wheelExponent : WheelStep.RelativeExponent(start != 0 ? start : _scale);
        if (exponent is not { } baseExponent)
            return; // relative steps of a value that has never been non-zero: no scale known

        var modifier = (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : 0)
                       - (e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 1 : 0);
        Change(WheelStep.Apply(start, Math.Sign(delta), baseExponent + modifier));
        Text = Show(_value);
        CaretIndex = Text.Length;
        _wheelValue = _value;
        _wheelExponent = baseExponent;
    }
}
