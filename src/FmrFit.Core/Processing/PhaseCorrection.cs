using MathNet.Numerics;

namespace FmrFit.Core.Processing;

/// <summary>
/// Lock-in phase correction. Changing the reference phase by Δ rotates the outputs:
/// <c>X' = X cos Δ + Y sin Δ</c>, <c>Y' = −X sin Δ + Y cos Δ</c>
/// (what the lock-in would have recorded with its phase set Δ higher).
/// </summary>
public static class PhaseCorrection
{
    /// <summary>Rotates X/Y by <paramref name="deltaDeg"/> degrees.</summary>
    public static (double[] X, double[] Y) Rotate(double[] x, double[] y, double deltaDeg)
    {
        if (x.Length != y.Length)
            throw new ArgumentException("X and Y must have the same length.");

        var (sin, cos) = Math.SinCos(deltaDeg * Math.PI / 180);
        var rx = new double[x.Length];
        var ry = new double[y.Length];
        for (var i = 0; i < x.Length; i++)
        {
            rx[i] = x[i] * cos + y[i] * sin;
            ry[i] = -x[i] * sin + y[i] * cos;
        }
        return (rx, ry);
    }

    /// <summary>
    /// Phase change Δ (degrees, in (−90, 90]) that minimizes the variation of Y after subtracting a linear
    /// background in field from it, i.e. puts as much of the resonance signal as possible into X.
    /// Δ + 180° gives the same result with the sign of X flipped.
    /// The background is used only inside this estimate; the data are not changed.
    /// </summary>
    /// <remarks>
    /// Subtracting the best straight line in field from Y' = −X sinΔ + Y cosΔ is a linear operation, so it equals
    /// rotating X and Y after removing their own linear-in-field parts. Then
    /// Var(Y') = Sxx sin²Δ − 2 Sxy sinΔ cosΔ + Syy cos²Δ (covariances of the residuals) is smallest when
    /// (cos Δ, sin Δ) is the major axis of that covariance, Δ = ½ atan2(2 Sxy, Sxx − Syy).
    /// </remarks>
    public static double EstimateMinYVariance(double[] field, double[] x, double[] y)
    {
        if (x.Length != y.Length || field.Length != x.Length || x.Length < 3)
            throw new ArgumentException("Field, X and Y must have the same length of at least 3.");

        var rx = SubtractLine(field, x);
        var ry = SubtractLine(field, y);
        double sxx = 0, syy = 0, sxy = 0;
        for (var i = 0; i < rx.Length; i++)
        {
            sxx += rx[i] * rx[i];
            syy += ry[i] * ry[i];
            sxy += rx[i] * ry[i];
        }

        var delta = 0.5 * Math.Atan2(2 * sxy, sxx - syy) * 180 / Math.PI;
        return delta <= -90 ? delta + 180 : delta;
    }

    /// <summary>Half-width of the estimation window, in units of <see cref="ResonanceWindow.HalfWidth"/>.</summary>
    public const double ResonanceWindowWidths = 5;

    private const int MinWindowPoints = 20;

    /// <summary>
    /// Phase estimate at the main resonance, using both sweeps, in this order: apply the sweep field correction
    /// (<see cref="SweepLagCorrection.ApplyFieldCorrection"/>); keep only points within
    /// ±<see cref="ResonanceWindowWidths"/> resonance half-widths of the resonance (<see cref="ResonanceWindow.Find"/>);
    /// remove a linear background in the corrected field; minimize the variation of Y
    /// (<see cref="EstimateMinYVariance(double[], double[], double[])"/>).
    /// The window keeps features away from the resonance (e.g. low-field absorption) from pulling the phase.
    /// Falls back to the whole field range if no resonance window with enough points is found.
    /// </summary>
    public static double EstimateAtResonance(FieldSweeps sweeps, double fieldCorrection)
    {
        var (upField, downField) = SweepLagCorrection.ApplyFieldCorrection(sweeps, fieldCorrection);
        double[] field = [.. upField, .. downField];
        double[] x = [.. sweeps.Up.X, .. sweeps.Down.X];
        double[] y = [.. sweeps.Up.Y, .. sweeps.Down.Y];

        var window = ResonanceWindow.Find(field, x, y);
        if (window is not null)
        {
            var inside = Enumerable.Range(0, field.Length)
                .Where(i => window.Contains(field[i], ResonanceWindowWidths)).ToArray();
            if (inside.Length >= MinWindowPoints)
                return EstimateMinYVariance(
                    inside.Select(i => field[i]).ToArray(),
                    inside.Select(i => x[i]).ToArray(),
                    inside.Select(i => y[i]).ToArray());
        }

        return EstimateMinYVariance(field, x, y);
    }

    /// <summary>Residuals of <paramref name="values"/> after subtracting their least-squares line in <paramref name="field"/>.</summary>
    private static double[] SubtractLine(double[] field, double[] values)
    {
        var (intercept, slope) = Fit.Line(field, values);
        var result = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
            result[i] = values[i] - (intercept + slope * field[i]);
        return result;
    }

    /// <summary>Wraps an angle in degrees to (−180, 180].</summary>
    public static double Normalize(double deg)
    {
        var r = deg % 360;
        if (r <= -180) r += 360;
        else if (r > 180) r -= 360;
        return r;
    }
}
