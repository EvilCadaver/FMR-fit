using MathNet.Numerics;

namespace FmrFit.Core.Processing;

/// <summary>
/// Field range around the main resonance: where the signal changes fastest with field.
/// </summary>
/// <param name="Center">Field (Oe) of the largest |d(X,Y)/dH|.</param>
/// <param name="HalfWidth">Half of the field range over which |d(X,Y)/dH| stays above half its maximum (Oe).</param>
public sealed record ResonanceWindow(double Center, double HalfWidth)
{
    /// <summary>Number of neighbouring points (on each side, in field order) used for the local slope fit.</summary>
    private const int SlopeHalfSpan = 10;

    public bool Contains(double field, double widths) => Math.Abs(field - Center) <= widths * HalfWidth;

    /// <summary>
    /// Locates the resonance. Uses the slope magnitude √((dX/dH)² + (dY/dH)²), which does not depend on the
    /// lock-in phase, from straight-line fits over neighbouring points sorted by field. Returns null if there
    /// are too few points.
    /// </summary>
    public static ResonanceWindow? Find(double[] field, double[] x, double[] y)
    {
        if (field.Length != x.Length || field.Length != y.Length)
            throw new ArgumentException("Field, X and Y must have the same length.");
        if (field.Length < 2 * SlopeHalfSpan + 3)
            return null;

        var order = Enumerable.Range(0, field.Length).OrderBy(i => field[i]).ToArray();
        var h = order.Select(i => field[i]).ToArray();
        var xs = order.Select(i => x[i]).ToArray();
        var ys = order.Select(i => y[i]).ToArray();

        var slope = new double[h.Length];
        for (var i = SlopeHalfSpan; i < h.Length - SlopeHalfSpan; i++)
        {
            var range = (i - SlopeHalfSpan)..(i + SlopeHalfSpan + 1);
            if (h[range.End.Value - 1] == h[range.Start.Value])
                continue;
            var (_, sx) = Fit.Line(h[range], xs[range]);
            var (_, sy) = Fit.Line(h[range], ys[range]);
            slope[i] = Math.Sqrt(sx * sx + sy * sy);
        }

        var peak = Array.IndexOf(slope, slope.Max());
        if (slope[peak] <= 0)
            return null;

        var lo = peak;
        var hi = peak;
        while (lo > SlopeHalfSpan && slope[lo] > slope[peak] / 2)
            lo--;
        while (hi < h.Length - SlopeHalfSpan - 1 && slope[hi] > slope[peak] / 2)
            hi++;

        var halfWidth = Math.Max((h[hi] - h[lo]) / 2, h[peak + 1] - h[peak - 1]);
        return new ResonanceWindow(h[peak], halfWidth);
    }
}
