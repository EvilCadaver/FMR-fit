namespace FmrFit.Core.Fitting;

/// <summary>
/// Keeps a peak's height when its width is changed by hand. Amplitudes that carry a field unit must scale with the
/// width to the same power for the height (data units) to stay the same: data units · Oe²
/// (<see cref="ParameterKind.AmplitudeFieldSquared"/>) with width², data units · Oe
/// (<see cref="ParameterKind.AmplitudeField"/>) with width. For the Lorentzian derivative (A1, A2 → s²·A1, s²·A2) and
/// the Lorentzian (A1, A2 → s·A1, s·A2) this is exact: with w → s·w the line keeps its shape and height and
/// is only stretched along the field, f'(xc + s·u) = f(xc + u). Amplitudes in data units
/// (<see cref="ParameterKind.Amplitude"/>, e.g. the Gaussians' A) already are the height and stay as they are.
/// </summary>
public static class PeakScaling
{
    /// <summary>Power of the width an amplitude of <paramref name="kind"/> scales with; 0 if it doesn't.</summary>
    public static int WidthPower(ParameterKind kind) => kind switch
    {
        ParameterKind.AmplitudeField => 1,
        ParameterKind.AmplitudeFieldSquared => 2,
        _ => 0,
    };

    /// <summary>True if changing a width of <paramref name="shape"/> changes its height, so scaling has an effect.</summary>
    public static bool Applies(IPeakShape shape) => shape.ParameterKinds.Any(k => WidthPower(k) > 0);

    /// <summary>
    /// The peak with width <paramref name="index"/> set to <paramref name="width"/> (clamped to its range) and every
    /// amplitude with a field unit multiplied by (new width / old width) to its <see cref="WidthPower"/>, so the peak
    /// height stays the same. An amplitude range is widened at the end the scaled amplitude crosses (never narrowed).
    /// If the old or new width is 0 the amplitudes are left unchanged. Errors are dropped.
    /// </summary>
    public static Peak WithWidthKeepingHeight(this Peak peak, int index, double width)
    {
        if (peak.Shape.ParameterKinds[index] != ParameterKind.Width)
            throw new ArgumentException("Parameter is not a width.", nameof(index));
        var oldWidth = Math.Abs(peak.Parameters[index]);
        var changed = peak.WithParameter(index, width);
        var newWidth = Math.Abs(changed.Parameters[index]);
        if (oldWidth == 0 || newWidth == 0 || oldWidth == newWidth)
            return changed;

        var ratio = newWidth / oldWidth;
        var parameters = (double[])changed.Parameters.Clone();
        var limits = (ParameterRange[])changed.LimitsOrDefault().Clone();
        for (var k = 0; k < parameters.Length; k++)
        {
            var power = WidthPower(peak.Shape.ParameterKinds[k]);
            if (power == 0)
                continue;
            parameters[k] *= power == 1 ? ratio : ratio * ratio;
            if (!limits[k].Contains(parameters[k]))
                limits[k] = new ParameterRange(Math.Min(limits[k].Min, parameters[k]), Math.Max(limits[k].Max, parameters[k]));
        }
        return changed with { Parameters = parameters, Limits = limits };
    }
}
