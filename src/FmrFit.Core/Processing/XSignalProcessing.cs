namespace FmrFit.Core.Processing;

/// <summary>Which sweeps of a measurement to use.</summary>
public enum SweepSelection
{
    UpAndDown,
    Up,
    Down,
}

/// <summary>Text form of <see cref="SweepSelection"/> as stored in work files.</summary>
public static class SweepSelectionText
{
    public static string ToText(SweepSelection selection) => selection switch
    {
        SweepSelection.Up => "up",
        SweepSelection.Down => "down",
        _ => "up&down",
    };

    /// <summary>Inverse of <see cref="ToText"/>; anything unrecognized is <see cref="SweepSelection.UpAndDown"/>.</summary>
    public static SweepSelection Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "up" => SweepSelection.Up,
        "down" => SweepSelection.Down,
        _ => SweepSelection.UpAndDown,
    };
}

/// <summary>Straight-line background <c>Intercept + Slope · v</c>.</summary>
public sealed record LinearBackground(double Intercept, double Slope)
{
    public static LinearBackground None { get; } = new(0, 0);

    public double At(double v) => Intercept + Slope * v;

    /// <summary>Least-squares line through (<paramref name="v"/>, <paramref name="values"/>); <see cref="None"/> if it cannot be fitted.</summary>
    public static LinearBackground Fit(double[] v, double[] values)
    {
        if (v.Length != values.Length)
            throw new ArgumentException("Inputs must have the same length.");
        if (v.Length < 2)
            return None;

        var (intercept, slope) = MathNet.Numerics.Fit.Line(v, values);
        return double.IsFinite(intercept) && double.IsFinite(slope) ? new(intercept, slope) : None;
    }
}

/// <summary>Regular field grid From, From + Step, … up to To (Oe).</summary>
public sealed record FieldGrid(double From, double To, double Step)
{
    public const double DefaultStep = 10;
    public const int MaxPoints = 1_000_000;

    public bool IsValid =>
        double.IsFinite(From) && double.IsFinite(To) && double.IsFinite(Step)
        && Step > 0 && To >= From && (To - From) / Step < MaxPoints;

    /// <summary>Grid points; empty if the grid is not valid.</summary>
    public double[] Points()
    {
        if (!IsValid)
            return [];
        var count = (int)Math.Floor((To - From) / Step + 1e-9) + 1;
        var points = new double[count];
        for (var i = 0; i < count; i++)
            points[i] = From + i * Step;
        return points;
    }
}

/// <summary>One sweep's values with their time stamps and (corrected) field.</summary>
public sealed record SignalBranch(double[] Time, double[] Field, double[] Values);

/// <summary>Settings of the processed-X pipeline, see <see cref="XSignalProcessing.Process"/>.</summary>
/// <param name="Scale">Final factor on the result, e.g. <see cref="PowerNormalization.Factor"/>; 1 for none.
/// Backgrounds are in the units of the unscaled signal.</param>
/// <param name="Smoothing">Smoothing of the interpolated signal; null for none.</param>
/// <param name="Display">Form of the result to show (<see cref="SignalCalculus.View"/>, applied after the pipeline);
/// <see cref="XSignalProcessing.Process"/> always returns the signal itself.</param>
/// <param name="ApplyOffset">Whether <paramref name="Offset"/> is added to the displayed form
/// (<see cref="SignalCalculus.Displayed"/>). The offset is kept either way.</param>
/// <param name="Offset">Offset for the displayed form, in its units after <paramref name="Scale"/> (V, V·Oe or V/Oe).</param>
public sealed record XProcessingSettings(
    SweepSelection Sweeps,
    LinearBackground TimeBackground,
    LinearBackground FieldBackground,
    FieldGrid Grid,
    double Scale = 1,
    SmoothingSettings? Smoothing = null,
    SignalView Display = SignalView.Signal,
    bool ApplyOffset = false,
    double Offset = 0);

/// <summary>
/// Processed X signal: select sweeps (with sweep field correction and phase applied) → subtract a linear
/// background in time → subtract a linear background in field → interpolate onto a regular field grid
/// → smooth (on the equally spaced grid values) → multiply by <see cref="XProcessingSettings.Scale"/>
/// (power normalization).
/// </summary>
public static class XSignalProcessing
{
    /// <summary>The whole pipeline.</summary>
    public static (double[] Field, double[] X) Process(
        FieldSweeps sweeps, double fieldCorrection, double phaseDeltaDeg, XProcessingSettings settings)
    {
        var branches = SubtractTimeBackground(
            Select(sweeps, fieldCorrection, phaseDeltaDeg, settings.Sweeps), settings.TimeBackground);
        var (field, x) = Interpolate(SubtractFieldBackground(branches, settings.FieldBackground), settings.Grid);
        if (settings.Smoothing is { Method: not SmoothingMethod.None } smoothing)
            x = Smoothing.Apply(x, smoothing);
        if (settings.Scale != 1)
            for (var i = 0; i < x.Length; i++)
                x[i] *= settings.Scale;
        return (field, x);
    }

    /// <summary>
    /// Field background for the pipeline: least-squares line in field through the selected X after the time
    /// background is removed, using only points inside the grid range.
    /// </summary>
    public static LinearBackground AutoFieldBackground(
        FieldSweeps sweeps, double fieldCorrection, double phaseDeltaDeg, XProcessingSettings settings)
    {
        var branches = SubtractTimeBackground(
            Select(sweeps, fieldCorrection, phaseDeltaDeg, settings.Sweeps), settings.TimeBackground);
        return FitFieldBackground(branches, settings.Grid.From, settings.Grid.To);
    }

    /// <summary>
    /// Grid covering the full field range of the selected sweeps together (after the sweep field correction),
    /// rounded inward to multiples of <paramref name="step"/>. Where only one sweep has data,
    /// <see cref="Interpolate"/> uses that sweep alone.
    /// </summary>
    public static FieldGrid GuessGrid(FieldSweeps sweeps, double fieldCorrection, SweepSelection selection,
        double step = FieldGrid.DefaultStep)
    {
        var branches = Select(sweeps, fieldCorrection, 0, selection);
        var lo = branches.Min(b => b.Field.Min());
        var hi = branches.Max(b => b.Field.Max());
        return new FieldGrid(Math.Ceiling(lo / step) * step, Math.Floor(hi / step) * step, step);
    }

    /// <summary>Selected sweeps with the sweep field correction applied and X rotated by the phase change.</summary>
    public static IReadOnlyList<SignalBranch> Select(
        FieldSweeps sweeps, double fieldCorrection, double phaseDeltaDeg, SweepSelection selection)
    {
        var (upField, downField) = SweepLagCorrection.ApplyFieldCorrection(sweeps, fieldCorrection);
        var branches = new List<SignalBranch>(2);
        if (selection != SweepSelection.Down)
            branches.Add(new(sweeps.Up.Time, upField, PhaseCorrection.Rotate(sweeps.Up.X, sweeps.Up.Y, phaseDeltaDeg).X));
        if (selection != SweepSelection.Up)
            branches.Add(new(sweeps.Down.Time, downField, PhaseCorrection.Rotate(sweeps.Down.X, sweeps.Down.Y, phaseDeltaDeg).X));
        return branches;
    }

    public static IReadOnlyList<SignalBranch> SubtractTimeBackground(
        IReadOnlyList<SignalBranch> branches, LinearBackground background) =>
        branches.Select(b => b with { Values = Subtract(b.Values, b.Time, background) }).ToList();

    public static IReadOnlyList<SignalBranch> SubtractFieldBackground(
        IReadOnlyList<SignalBranch> branches, LinearBackground background) =>
        branches.Select(b => b with { Values = Subtract(b.Values, b.Field, background) }).ToList();

    /// <summary>Least-squares line in field through all points of all branches with field in [from, to].</summary>
    public static LinearBackground FitFieldBackground(IReadOnlyList<SignalBranch> branches, double from, double to)
    {
        var field = new List<double>();
        var values = new List<double>();
        foreach (var b in branches)
            for (var i = 0; i < b.Field.Length; i++)
                if (b.Field[i] >= from && b.Field[i] <= to)
                {
                    field.Add(b.Field[i]);
                    values.Add(b.Values[i]);
                }
        return LinearBackground.Fit([.. field], [.. values]);
    }

    /// <summary>
    /// Linear interpolation of each branch onto the grid; where several branches cover a grid point their
    /// values are averaged. Grid points not covered by any branch are left out.
    /// </summary>
    public static (double[] Field, double[] Values) Interpolate(IReadOnlyList<SignalBranch> branches, FieldGrid grid)
    {
        var series = branches.Where(b => b.Field.Length > 0).Select(b => new SortedSeries(b.Field, b.Values)).ToList();
        var field = new List<double>();
        var values = new List<double>();
        foreach (var h in grid.Points())
        {
            double sum = 0;
            var n = 0;
            foreach (var s in series)
                if (s.Covers(h))
                {
                    sum += s.Evaluate(h);
                    n++;
                }
            if (n == 0)
                continue;
            field.Add(h);
            values.Add(sum / n);
        }
        return ([.. field], [.. values]);
    }

    private static double[] Subtract(double[] values, double[] v, LinearBackground background)
    {
        var result = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
            result[i] = values[i] - background.At(v[i]);
        return result;
    }
}
