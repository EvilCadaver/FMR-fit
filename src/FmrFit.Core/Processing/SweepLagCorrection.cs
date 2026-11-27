namespace FmrFit.Core.Processing;

/// <summary>Result of <see cref="SweepLagCorrection.Estimate"/>.</summary>
/// <param name="Delay">Estimated signal delay τ (s).</param>
/// <param name="FieldCorrection">The same correction as a field shift (Oe), see <see cref="SweepLagCorrection.FieldCorrectionFor"/>.</param>
/// <param name="Mismatch">Up/down mismatch at <paramref name="Delay"/>.</param>
/// <param name="MismatchWithoutCorrection">Up/down mismatch at τ = 0.</param>
/// <param name="AtSearchLimit">The delay is at an end of the search window: the sweeps probably match best outside
/// it, so the estimate failed and <paramref name="Delay"/> is just the nearest end.</param>
public sealed record LagEstimate(
    double Delay, double FieldCorrection, double Mismatch, double MismatchWithoutCorrection, bool AtSearchLimit = false)
{
    /// <summary>Fraction by which the correction reduces the mismatch (0 = no improvement, 1 = perfect overlap).
    /// Close to 0 when there is no resonance peak to align.</summary>
    public double Reduction => MismatchWithoutCorrection > 0 ? 1 - Mismatch / MismatchWithoutCorrection : 0;
}

/// <summary>
/// Corrects the field axis for the lag between the field reading and the (lock-in filtered) signal.
/// A point recorded at time t reflects the field at t − τ, i.e. H(t) − τ·dH/dt, so peaks are shifted
/// to higher field on the up sweep and to lower field on the down sweep.
/// </summary>
public static class SweepLagCorrection
{
    /// <summary>A negative delay (signal ahead of the field) is not physical, so the default window starts at 0.</summary>
    public const double DefaultMinDelay = 0.0;
    public const double DefaultMaxDelay = 5.0;
    private const double CoarseStep = 0.02;
    private const int GridPoints = 2000;

    /// <summary>
    /// Applies a field correction (Oe): <c>H − correction</c> on the up sweep, <c>H + correction</c> on the down sweep.
    /// </summary>
    public static (double[] Up, double[] Down) ApplyFieldCorrection(FieldSweeps sweeps, double correction) =>
        (Shift(sweeps.Up.Field, -correction), Shift(sweeps.Down.Field, correction));

    /// <summary>Field correction (Oe) equivalent to a delay τ, using the mean of the up and down sweep speeds.</summary>
    public static double FieldCorrectionFor(FieldSweeps sweeps, double delay) =>
        delay * (sweeps.UpRate - sweeps.DownRate) / 2;

    private static double[] Shift(double[] field, double shift)
    {
        var result = new double[field.Length];
        for (var i = 0; i < result.Length; i++)
            result[i] = field[i] + shift;
        return result;
    }

    /// <summary>
    /// Finds the delay at which the corrected up and down sweeps overlap best (smallest mismatch of X and Y,
    /// ignoring a constant offset between the sweeps). Only meaningful if the data contain a resonance peak;
    /// check <see cref="LagEstimate.Reduction"/>.
    /// </summary>
    public static LagEstimate Estimate(FieldSweeps sweeps,
        double minDelay = DefaultMinDelay, double maxDelay = DefaultMaxDelay)
    {
        var up = new SortedSignal(sweeps.Up);
        var down = new SortedSignal(sweeps.Down);
        double Cost(double tau) => Mismatch(up, down, sweeps.UpRate, sweeps.DownRate, tau);

        // Coarse scan to find the right valley, then refine inside it.
        var best = minDelay;
        var bestCost = double.PositiveInfinity;
        for (var tau = minDelay; tau <= maxDelay + 1e-12; tau += CoarseStep)
        {
            var c = Cost(tau);
            if (c < bestCost)
            {
                bestCost = c;
                best = tau;
            }
        }

        var lo = Math.Max(minDelay, best - CoarseStep);
        var hi = Math.Min(maxDelay, best + CoarseStep);
        if (hi > lo)
        {
            var (tau, cost) = GoldenSection(Cost, lo, hi, RefineTolerance);
            if (cost < bestCost)
                (best, bestCost) = (tau, cost);
        }

        var atLimit = best <= minDelay + RefineTolerance || best >= maxDelay - RefineTolerance;
        return new LagEstimate(best, FieldCorrectionFor(sweeps, best), bestCost, Cost(0), atLimit);
    }

    private const double RefineTolerance = 1e-4;

    /// <summary>
    /// Golden-section search for a minimum of <paramref name="f"/> that never leaves [<paramref name="lo"/>,
    /// <paramref name="hi"/>] and never fails: if the minimum lies at an end (e.g. the best coarse delay is at the edge
    /// of the search window), it converges to that end.
    /// </summary>
    private static (double X, double Value) GoldenSection(Func<double, double> f, double lo, double hi, double tolerance)
    {
        var ratio = (Math.Sqrt(5) - 1) / 2;
        var a = hi - ratio * (hi - lo);
        var b = lo + ratio * (hi - lo);
        double fa = f(a), fb = f(b);
        while (hi - lo > tolerance)
        {
            if (fa <= fb)
            {
                (hi, b, fb) = (b, a, fa);
                a = hi - ratio * (hi - lo);
                fa = f(a);
            }
            else
            {
                (lo, a, fa) = (a, b, fb);
                b = lo + ratio * (hi - lo);
                fb = f(b);
            }
        }
        var x = (lo + hi) / 2;
        return (x, f(x));
    }

    /// <summary>
    /// Mean squared difference between corrected up and down X and Y on their common field range,
    /// after removing the mean difference.
    /// </summary>
    public static double Mismatch(FieldSweeps sweeps, double delay) =>
        Mismatch(new SortedSignal(sweeps.Up), new SortedSignal(sweeps.Down), sweeps.UpRate, sweeps.DownRate, delay);

    private static double Mismatch(SortedSignal up, SortedSignal down, double upRate, double downRate, double delay)
    {
        var upShift = delay * upRate;
        var downShift = delay * downRate;
        var lo = Math.Max(up.MinField - upShift, down.MinField - downShift);
        var hi = Math.Min(up.MaxField - upShift, down.MaxField - downShift);
        if (hi <= lo)
            return double.PositiveInfinity;

        var diffX = new double[GridPoints];
        var diffY = new double[GridPoints];
        for (var i = 0; i < GridPoints; i++)
        {
            var h = lo + (hi - lo) * i / (GridPoints - 1);
            up.Evaluate(h + upShift, out var xu, out var yu);
            down.Evaluate(h + downShift, out var xd, out var yd);
            diffX[i] = xu - xd;
            diffY[i] = yu - yd;
        }

        return Variance(diffX) + Variance(diffY);
    }

    private static double Variance(double[] values)
    {
        var mean = values.Average();
        var sum = 0.0;
        foreach (var v in values)
            sum += (v - mean) * (v - mean);
        return sum / values.Length;
    }

    /// <summary>X and Y of a branch, each sorted by field for interpolation.</summary>
    private sealed class SortedSignal(SweepBranch branch)
    {
        private readonly SortedSeries _x = new(branch.Field, branch.X);
        private readonly SortedSeries _y = new(branch.Field, branch.Y);

        public double MinField => _x.MinField;
        public double MaxField => _x.MaxField;

        public void Evaluate(double h, out double x, out double y)
        {
            x = _x.Evaluate(h);
            y = _y.Evaluate(h);
        }
    }
}
