using System.Globalization;
using FmrFit.Core.IO;

namespace FmrFit.Core.Processing;

/// <summary>Which form of the processed signal is shown: the signal itself, its integral or its derivative over field.</summary>
public enum SignalView
{
    Signal,
    Integral,
    Derivative,
}

/// <summary>Text form of <see cref="SignalView"/> as stored in work files.</summary>
public static class SignalViewText
{
    public static string ToText(SignalView view) => view switch
    {
        SignalView.Integral => "integral",
        SignalView.Derivative => "derivative",
        _ => "signal",
    };

    /// <summary>Inverse of <see cref="ToText"/>; anything unrecognized (including a missing entry) is the signal.</summary>
    public static SignalView Parse(string? text) => text?.Trim().ToLower(CultureInfo.InvariantCulture) switch
    {
        "integral" => SignalView.Integral,
        "derivative" => SignalView.Derivative,
        _ => SignalView.Signal,
    };
}

/// <summary>Integral and derivative of a signal over field. The field must increase; spacing may vary.</summary>
public static class SignalCalculus
{
    /// <summary>Running trapezoid integral from the first point (where it is 0). Units: signal · field.</summary>
    public static double[] Integrate(double[] field, double[] values)
    {
        CheckLengths(field, values);
        var result = new double[values.Length];
        for (var i = 1; i < values.Length; i++)
            result[i] = result[i - 1] + 0.5 * (values[i] + values[i - 1]) * (field[i] - field[i - 1]);
        return result;
    }

    /// <summary>
    /// Derivative by second-order central differences (exact for a parabola, also on an uneven grid) and one-sided
    /// differences at the two end points. Units: signal / field. Fewer than two points give zeros.
    /// </summary>
    public static double[] Differentiate(double[] field, double[] values)
    {
        CheckLengths(field, values);
        var n = values.Length;
        var result = new double[n];
        if (n < 2)
            return result;

        result[0] = (values[1] - values[0]) / (field[1] - field[0]);
        result[n - 1] = (values[n - 1] - values[n - 2]) / (field[n - 1] - field[n - 2]);
        for (var i = 1; i < n - 1; i++)
        {
            var before = field[i] - field[i - 1];
            var after = field[i + 1] - field[i];
            result[i] = (before * before * values[i + 1] + (after * after - before * before) * values[i]
                         - after * after * values[i - 1]) / (before * after * (before + after));
        }
        return result;
    }

    /// <summary>Power of Oe in the view's unit: signal mV (0), integral mV·Oe (1), derivative mV/Oe (−1).</summary>
    public static int FieldPower(SignalView view) => view switch
    {
        SignalView.Integral => 1,
        SignalView.Derivative => -1,
        _ => 0,
    };

    /// <summary>The signal, its integral or its derivative.</summary>
    public static double[] View(double[] field, double[] values, SignalView view) => view switch
    {
        SignalView.Integral => Integrate(field, values),
        SignalView.Derivative => Differentiate(field, values),
        _ => values,
    };

    /// <summary>Offset that moves the minimum of <paramref name="values"/> to 0 (non-finite values ignored); 0 if none.</summary>
    public static double ZeroMinimumOffset(double[] values)
    {
        var min = double.PositiveInfinity;
        foreach (var v in values)
            if (double.IsFinite(v) && v < min)
                min = v;
        return double.IsFinite(min) ? -min : 0;
    }

    /// <summary>Whether "offset to 0" starts switched on: for an integral or a recorded ~P, where the baseline of an
    /// absorption line should be 0.</summary>
    public static bool DefaultApplyOffset(RecordedSignal recorded, SignalView view) =>
        view == SignalView.Integral || recorded == RecordedSignal.Absorption;

    /// <summary>
    /// The displayed form of <paramref name="values"/> (<see cref="View"/>), plus <paramref name="offset"/> if
    /// <paramref name="applyOffset"/>. Always a new array.
    /// </summary>
    public static double[] Displayed(double[] field, double[] values, SignalView view, bool applyOffset, double offset) =>
        Shifted(View(field, values, view), applyOffset ? offset : 0);

    /// <summary>
    /// What the work file shows, fits and exports: its displayed column (<see cref="DisplayedView"/>) plus
    /// <see cref="WorkFileProcessing.DisplayOffset"/> if <see cref="WorkFileProcessing.DisplayOffsetApplied"/>.
    /// The stored columns themselves never include the offset.
    /// </summary>
    public static double[] DisplayedColumn(WorkFile file)
    {
        var p = file.Processing;
        return Shifted(Column(file, DisplayedView(file)), p.DisplayOffsetApplied ? p.DisplayOffset : 0);
    }

    private static double[] Shifted(double[] values, double offset)
    {
        var result = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
            result[i] = values[i] + offset;
        return result;
    }

    /// <summary>
    /// The work file's column for <paramref name="view"/>, as calculated (without any display offset); for files
    /// written before the integral and derivative columns existed they are calculated from the signal.
    /// </summary>
    public static double[] Column(WorkFile file, SignalView view) => view switch
    {
        SignalView.Integral => file.IntegralMvOe ?? Integrate(file.FieldOe, file.SignalMv),
        SignalView.Derivative => file.DerivativeMvPerOe ?? Differentiate(file.FieldOe, file.SignalMv),
        _ => file.SignalMv,
    };

    /// <summary>The column chosen for display in the work file (<see cref="WorkFileProcessing.Display"/>).</summary>
    public static SignalView DisplayedView(WorkFile file) => SignalViewText.Parse(file.Processing.Display);

    private static void CheckLengths(double[] field, double[] values)
    {
        if (field.Length != values.Length)
            throw new ArgumentException("Field and values must have the same length.");
    }
}
