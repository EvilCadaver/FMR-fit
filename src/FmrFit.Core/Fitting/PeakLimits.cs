namespace FmrFit.Core.Fitting;

/// <summary>Allowed range [Min, Max] of one peak parameter; Min ≤ Max. Either end may be infinite.</summary>
public readonly record struct ParameterRange(double Min, double Max)
{
    public static ParameterRange Unbounded => new(double.NegativeInfinity, double.PositiveInfinity);

    public double Clamp(double value) => Math.Clamp(value, Min, Max);

    public bool Contains(double value) => value >= Min && value <= Max;
}

/// <summary>
/// Limits on the parameters of a peak; they bound manual changes and the fit. Widths have a single limit L, giving
/// [<see cref="MinWidth"/>, L]; positions and amplitudes have an independent minimum and maximum (amplitudes start
/// symmetric, [−L, L]).
/// </summary>
public static class PeakLimits
{
    /// <summary>Default limit: this many times the largest |value| of the same kind in the peak.</summary>
    public const double DefaultFactor = 10;

    /// <summary>
    /// Hard lower bound of every width (Oe), whatever the stored limits say: no range a width gets from
    /// <see cref="LimitsOrDefault"/> reaches below it, so neither manual changes nor the fit can go lower.
    /// </summary>
    public const double MinWidth = 0.1;

    /// <summary>
    /// Default ranges. Widths and amplitudes: limit <paramref name="factor"/> × the largest |value| among the
    /// peak's parameters of the same kind (so A2 = 0 gets A1's limit), or 1 if those are all 0. Positions:
    /// [<paramref name="fieldMin"/>, <paramref name="fieldMax"/>], unbounded if not given.
    /// </summary>
    public static ParameterRange[] Default(IPeakShape shape, IReadOnlyList<double> parameters,
        double fieldMin = double.NegativeInfinity, double fieldMax = double.PositiveInfinity, double factor = DefaultFactor)
    {
        var kinds = shape.ParameterKinds;
        var ranges = new ParameterRange[parameters.Count];
        for (var i = 0; i < ranges.Length; i++)
        {
            if (kinds[i] == ParameterKind.Position)
            {
                ranges[i] = new ParameterRange(Math.Min(fieldMin, fieldMax), Math.Max(fieldMin, fieldMax));
                continue;
            }
            var largest = 0.0;
            for (var j = 0; j < ranges.Length; j++)
                if (kinds[j] == kinds[i] && double.IsFinite(parameters[j]))
                    largest = Math.Max(largest, Math.Abs(parameters[j]));
            ranges[i] = FromLimit(kinds[i], largest > 0 ? factor * largest : 1);
        }
        return ranges;
    }

    /// <summary>
    /// Range given by a single limit: [<see cref="MinWidth"/>, limit] for widths (a limit below
    /// <see cref="MinWidth"/> gives [MinWidth, MinWidth]), [−limit, limit] for amplitudes.
    /// </summary>
    public static ParameterRange FromLimit(ParameterKind kind, double limit)
    {
        if (kind == ParameterKind.Position)
            throw new ArgumentException("Positions have a minimum and maximum, not a single limit.", nameof(kind));
        if (!double.IsFinite(limit) || limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be positive and finite.");
        return kind == ParameterKind.Width ? WidthRange(new ParameterRange(0, limit)) : new ParameterRange(-limit, limit);
    }

    /// <summary>
    /// The peak's ranges, or <see cref="Default"/> ones if it has none; width ranges are raised to at least
    /// <see cref="MinWidth"/> (e.g. ranges [0, L] from older fit files).
    /// </summary>
    public static ParameterRange[] LimitsOrDefault(this Peak peak)
    {
        if (peak.Limits is not { } limits)
            return Default(peak.Shape, peak.Parameters);
        var kinds = peak.Shape.ParameterKinds;
        ParameterRange[]? raised = null;
        for (var i = 0; i < limits.Length; i++)
        {
            if (kinds[i] != ParameterKind.Width || limits[i].Min >= MinWidth)
                continue;
            raised ??= (ParameterRange[])limits.Clone();
            raised[i] = WidthRange(limits[i]);
        }
        return raised ?? limits;
    }

    /// <summary>
    /// The peak with every width at least <see cref="MinWidth"/> and its width ranges raised to match; errors are
    /// dropped only if a width had to be raised. For peaks from guesses and fit files.
    /// </summary>
    public static Peak WithMinimumWidths(this Peak peak)
    {
        var result = peak.Limits is null ? peak : peak with { Limits = peak.LimitsOrDefault() };
        for (var k = 0; k < peak.Parameters.Length; k++)
            if (peak.Shape.ParameterKinds[k] == ParameterKind.Width && Math.Abs(peak.Parameters[k]) < MinWidth)
                result = result.WithParameter(k, MinWidth);
        return result;
    }

    /// <summary>A width range with both ends at least <see cref="MinWidth"/>.</summary>
    private static ParameterRange WidthRange(ParameterRange range) =>
        new(Math.Max(range.Min, MinWidth), Math.Max(range.Max, MinWidth));

    /// <summary>Allowed range of parameter <paramref name="index"/> of <paramref name="peak"/>.</summary>
    public static ParameterRange Range(this Peak peak, int index) => peak.LimitsOrDefault()[index];

    /// <summary>
    /// The peak with parameter <paramref name="index"/> set to <paramref name="value"/>, clamped to its range. Errors
    /// are dropped because they belong to the fitted values. Widths are stored as absolute values.
    /// </summary>
    public static Peak WithParameter(this Peak peak, int index, double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), "Value must be finite.");
        var limits = peak.LimitsOrDefault();
        if (peak.Shape.ParameterKinds[index] == ParameterKind.Width)
            value = Math.Abs(value);
        var parameters = (double[])peak.Parameters.Clone();
        parameters[index] = limits[index].Clamp(value);
        return peak with { Parameters = parameters, Errors = null, Limits = limits };
    }

    /// <summary>
    /// The peak with the single limit of width or amplitude <paramref name="index"/> set to <paramref name="limit"/>
    /// (&gt; 0); the parameter is clamped into the new range.
    /// </summary>
    public static Peak WithLimit(this Peak peak, int index, double limit) =>
        peak.WithRange(index, FromLimit(peak.Shape.ParameterKinds[index], limit));

    /// <summary>
    /// The peak with the minimum of parameter <paramref name="index"/> set to <paramref name="min"/>, but never above
    /// the maximum; the parameter is clamped into the new range.
    /// </summary>
    public static Peak WithMinimum(this Peak peak, int index, double min)
    {
        if (!double.IsFinite(min))
            throw new ArgumentOutOfRangeException(nameof(min), "Minimum must be finite.");
        var range = peak.Range(index);
        return peak.WithRange(index, range with { Min = Math.Min(min, range.Max) });
    }

    /// <summary>
    /// The peak with the maximum of parameter <paramref name="index"/> set to <paramref name="max"/>, but never below
    /// the minimum; the parameter is clamped into the new range.
    /// </summary>
    public static Peak WithMaximum(this Peak peak, int index, double max)
    {
        if (!double.IsFinite(max))
            throw new ArgumentOutOfRangeException(nameof(max), "Maximum must be finite.");
        var range = peak.Range(index);
        return peak.WithRange(index, range with { Max = Math.Max(max, range.Min) });
    }

    /// <summary>Sets one range; the value is clamped (and errors dropped) only if it falls outside.</summary>
    private static Peak WithRange(this Peak peak, int index, ParameterRange range)
    {
        var limits = (ParameterRange[])peak.LimitsOrDefault().Clone();
        limits[index] = range;
        var limited = peak with { Limits = limits };
        var value = peak.Parameters[index];
        if (peak.Shape.ParameterKinds[index] == ParameterKind.Width)
            value = Math.Abs(value);
        return range.Contains(value) ? limited : limited.WithParameter(index, value);
    }
}
