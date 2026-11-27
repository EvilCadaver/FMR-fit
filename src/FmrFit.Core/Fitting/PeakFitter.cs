using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;

namespace FmrFit.Core.Fitting;

/// <summary>One peak of a fit: its shape, parameters and (after fitting) their standard errors.</summary>
/// <param name="Errors">Standard errors from the last fit; NaN for parameters that were fixed.</param>
/// <param name="Limits">
/// Allowed range of each parameter (see <see cref="PeakLimits"/>); bounds manual changes and the fit.
/// Null means <see cref="PeakLimits.Default"/>.
/// </param>
/// <param name="Fixed">Parameters the fit leaves unchanged; null means none.</param>
/// <param name="KeepHeight">
/// When a width is changed by hand, scale the amplitudes so the peak height stays the same (see
/// <see cref="PeakScaling"/>). Not used by the fit.
/// </param>
public sealed record Peak(
    IPeakShape Shape,
    double[] Parameters,
    double[]? Errors = null,
    ParameterRange[]? Limits = null,
    bool[]? Fixed = null,
    bool KeepHeight = false)
{
    public double Evaluate(double field) => Shape.Evaluate(field, Parameters);

    public double[] Evaluate(double[] field) => field.Select(Evaluate).ToArray();

    public bool IsFixed(int index) => Fixed is { } f && f[index];

    /// <summary>True if every parameter is fixed.</summary>
    public bool AllFixed => Enumerable.Range(0, Parameters.Length).All(IsFixed);

    /// <summary>Number of parameters the fit may change.</summary>
    public int FreeParameterCount => Enumerable.Range(0, Parameters.Length).Count(i => !IsFixed(i));

    /// <summary>The peak with parameter <paramref name="index"/> fixed or freed for fitting; values and errors are kept.</summary>
    public Peak WithFixed(int index, bool isFixed)
    {
        var flags = Fixed is { } f ? (bool[])f.Clone() : new bool[Parameters.Length];
        flags[index] = isFixed;
        return this with { Fixed = flags };
    }

    /// <summary>The peak with all parameters fixed or all freed.</summary>
    public Peak WithAllFixed(bool isFixed) => this with { Fixed = Enumerable.Repeat(isFixed, Parameters.Length).ToArray() };
}

/// <summary>
/// Model (sum of peaks) compared with the data, with goodness-of-fit numbers. The data have no per-point
/// uncertainties, so as in Origin every point has weight 1: χ² is the residual sum of squares (in data units²) and the
/// reduced χ² = χ²/(N − p) is the residual variance. Only free (not fixed) parameters count in p.
/// </summary>
/// <param name="Fit">Sum of all peaks at each field.</param>
/// <param name="Residual">Data − fit at each field.</param>
/// <param name="Points">N, the number of data points.</param>
/// <param name="FreeParameters">p, the number of parameters the fit may change.</param>
/// <param name="ChiSquared">χ² = Σ (data − fit)².</param>
/// <param name="ReducedChiSquared">χ² / (N − p); NaN if N ≤ p.</param>
/// <param name="RSquared">R² = 1 − χ² / Σ (data − mean)²: 1 for a perfect fit; NaN for constant data.</param>
/// <param name="Iterations">Optimizer iterations; 0 if the peaks were only evaluated.</param>
/// <param name="ExitReason">Why the optimizer stopped, or empty if the peaks were only evaluated.</param>
public sealed record FitResult(
    IReadOnlyList<Peak> Peaks,
    double[] Fit,
    double[] Residual,
    int Points,
    int FreeParameters,
    double ChiSquared,
    double ReducedChiSquared,
    double RSquared,
    int Iterations,
    string ExitReason)
{
    /// <summary>σ = √(reduced χ²), the standard deviation of data − fit; NaN if N ≤ p.</summary>
    public double StandardDeviation => Math.Sqrt(ReducedChiSquared);
}

/// <summary>Least-squares fit of a sum of peaks to data (Levenberg–Marquardt).</summary>
public static class PeakFitter
{
    /// <summary>Default cap on optimizer iterations per fit.</summary>
    public const int DefaultMaxIterations = 2000;

    /// <summary>Compares the peaks with the data without changing them.</summary>
    public static FitResult Evaluate(double[] field, double[] signal, IReadOnlyList<Peak> peaks) =>
        Compare(signal, Sum(field, peaks), peaks);

    /// <summary>
    /// Compares the peaks with the data when their sum <paramref name="fit"/> (indexed like
    /// <paramref name="signal"/>) is already known, e.g. from <see cref="PeakCurves"/>.
    /// </summary>
    public static FitResult Compare(double[] signal, double[] fit, IReadOnlyList<Peak> peaks)
    {
        if (fit.Length != signal.Length)
            throw new ArgumentException("Fit and signal must have the same length.");
        var residual = signal.Select((v, i) => v - fit[i]).ToArray();
        var points = residual.Length;
        var free = peaks.Sum(p => p.FreeParameterCount);
        var chiSquared = residual.Sum(r => r * r);
        var dof = points - free;
        var mean = points > 0 ? signal.Average() : 0;
        var total = signal.Sum(v => (v - mean) * (v - mean));
        return new FitResult(peaks, fit, residual, points, free, chiSquared,
            dof > 0 ? chiSquared / dof : double.NaN,
            total > 0 ? 1 - chiSquared / total : double.NaN,
            0, "");
    }

    /// <summary>
    /// A new peak of <paramref name="shape"/> placed on what <paramref name="existing"/> peaks leave unexplained, with
    /// default limits; its position is limited to the measured field range and its widths are at least
    /// <see cref="PeakLimits.MinWidth"/>. Height keeping is on. <paramref name="limitFactor"/> sets the default width and
    /// amplitude limits (see <see cref="PeakLimits.Default"/>); shapes with their own limits ignore it.
    /// </summary>
    public static Peak GuessPeak(IPeakShape shape, double[] field, double[] signal, IReadOnlyList<Peak> existing,
        double limitFactor = PeakLimits.DefaultFactor)
    {
        var residual = Evaluate(field, signal, existing).Residual;
        var parameters = shape.Guess(field, residual);
        var limits = shape.GuessLimits(field, residual, parameters)
            ?? (field.Length > 0
                ? PeakLimits.Default(shape, parameters, field.Min(), field.Max(), limitFactor)
                : PeakLimits.Default(shape, parameters, factor: limitFactor));
        return new Peak(shape, parameters, Limits: limits, KeepHeight: true).WithMinimumWidths();
    }

    /// <summary>
    /// Fits all free parameters of all peaks together, starting from their current values; fixed parameters
    /// (<see cref="Peak.Fixed"/>) keep their values and get NaN errors. Every parameter stays within its peak's limits
    /// (<see cref="Peak.Limits"/>); an unbounded position end is replaced by the measured field range extended by half
    /// its span. Fields and amplitudes are scaled to order 1 internally, so parameters of very different magnitudes
    /// converge alike. The optimizer stops after at most <paramref name="maxIterations"/> iterations.
    /// </summary>
    public static FitResult Fit(double[] field, double[] signal, IReadOnlyList<Peak> peaks,
        int maxIterations = DefaultMaxIterations)
    {
        if (field.Length != signal.Length)
            throw new ArgumentException("Field and signal must have the same length.");
        var parameterCount = peaks.Sum(p => p.Parameters.Length);
        var freeCount = peaks.Sum(p => p.FreeParameterCount);
        if (peaks.Count == 0 || freeCount == 0 || field.Length <= freeCount)
            return Evaluate(field, signal, peaks);

        var fieldScale = Math.Max(field.Max(Math.Abs), 1e-12);
        var signalScale = Math.Max(signal.Max(Math.Abs), 1e-300);
        double Scale(ParameterKind kind) => kind switch
        {
            ParameterKind.Amplitude => signalScale,
            ParameterKind.AmplitudeField => signalScale * fieldScale,
            ParameterKind.AmplitudeFieldSquared => signalScale * fieldScale * fieldScale,
            ParameterKind.Slope => signalScale / fieldScale,
            _ => fieldScale,
        };
        var scales = peaks.SelectMany(p => p.Shape.ParameterKinds.Select(Scale)).ToArray();

        // Bounds in real units; MathNet's bounded minimizer needs finite bounds for every parameter.
        var fieldMin = field.Min();
        var fieldMax = field.Max();
        var span = Math.Max(fieldMax - fieldMin, 1e-12);
        // All parameters in one flat vector (in real units); only the free ones go to the minimizer.
        var all = peaks.SelectMany(p => p.Parameters).ToArray();
        var free = new List<int>(freeCount);
        var lower = new List<double>(freeCount);
        var upper = new List<double>(freeCount);
        var start = new List<double>(freeCount);
        var offset = 0;
        foreach (var peak in peaks)
        {
            var limits = peak.LimitsOrDefault();
            for (var k = 0; k < peak.Parameters.Length; k++, offset++)
            {
                if (peak.IsFixed(k))
                    continue;
                var kind = peak.Shape.ParameterKinds[k];
                var (min, max) = limits[k];
                if (kind == ParameterKind.Position)
                {
                    if (!double.IsFinite(min)) min = Math.Min(fieldMin - span / 2, max);
                    if (!double.IsFinite(max)) max = Math.Max(fieldMax + span / 2, min);
                }
                if (!double.IsFinite(min) || !double.IsFinite(max))
                    (min, max) = PeakLimits.Default(peak.Shape, peak.Parameters)[k];
                var value = kind == ParameterKind.Width ? Math.Abs(peak.Parameters[k]) : peak.Parameters[k];
                if (max <= min)
                {
                    // Minimum = maximum: the parameter is held (as good as) fixed; the minimizer needs a non-empty range.
                    var eps = 1e-9 * Math.Max(Math.Abs(min), scales[offset]);
                    (min, max) = (min - eps, max + eps);
                }
                // Strictly inside: at a bound the minimizer's sine transform has zero slope and the parameter sticks.
                var margin = 1e-6 * (max - min);
                free.Add(offset);
                lower.Add(min / scales[offset]);
                upper.Add(max / scales[offset]);
                start.Add(Math.Clamp(value, min + margin, max - margin) / scales[offset]);
            }
        }

        // Full parameter vector in real units: fixed values as given, free ones from the minimizer's scaled vector.
        double[] Expand(IEnumerable<double> scaledFree)
        {
            var values = (double[])all.Clone();
            var j = 0;
            foreach (var v in scaledFree)
            {
                values[free[j]] = v * scales[free[j]];
                j++;
            }
            return values;
        }
        var x = Vector<double>.Build.DenseOfArray(field.Select(h => h / fieldScale).ToArray());
        var y = Vector<double>.Build.DenseOfArray(signal.Select(v => v / signalScale).ToArray());

        // Model in scaled units: positions and widths in units of fieldScale, amplitudes in units of signalScale.
        Vector<double> Model(Vector<double> scaled, Vector<double> scaledField)
        {
            var real = Unpack(peaks, Expand(scaled));
            var result = Vector<double>.Build.Dense(scaledField.Count);
            for (var i = 0; i < scaledField.Count; i++)
            {
                var h = scaledField[i] * fieldScale;
                var sum = 0.0;
                foreach (var peak in real)
                    sum += peak.Evaluate(h);
                result[i] = sum / signalScale;
            }
            return result;
        }

        var objective = ObjectiveFunction.NonlinearModel(Model, x, y, accuracyOrder: 2);
        var minimizer = new LevenbergMarquardtMinimizer(maximumIterations: maxIterations);
        var result = minimizer.FindMinimum(objective, start.ToArray(), lower.ToArray(), upper.ToArray());

        var fitted = Expand(result.MinimizingPoint);
        double[]? errors = null;
        if (result.StandardErrors is { } standardErrors)
        {
            errors = Enumerable.Repeat(double.NaN, parameterCount).ToArray();
            for (var j = 0; j < free.Count; j++)
                errors[free[j]] = standardErrors[j] * scales[free[j]];
        }
        var fittedPeaks = Unpack(peaks, fitted, errors);

        var evaluated = Evaluate(field, signal, fittedPeaks);
        return evaluated with { Iterations = result.Iterations, ExitReason = result.ReasonForExit.ToString() };
    }

    /// <summary>
    /// Fits only peak <paramref name="index"/>, within its limits; all other peaks are locked and returned unchanged
    /// (including their errors). Equivalent to fitting that one peak to the data minus the other peaks.
    /// </summary>
    public static FitResult FitPeak(double[] field, double[] signal, IReadOnlyList<Peak> peaks, int index,
        int maxIterations = DefaultMaxIterations)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, peaks.Count);
        var others = peaks.Where((_, i) => i != index).ToList();
        var rest = Evaluate(field, signal, others).Residual;
        var single = Fit(field, rest, [peaks[index]], maxIterations);

        var fitted = peaks.ToList();
        fitted[index] = single.Peaks[0];
        return Evaluate(field, signal, fitted) with { Iterations = single.Iterations, ExitReason = single.ExitReason };
    }

    private static double[] Sum(double[] field, IReadOnlyList<Peak> peaks)
    {
        var sum = new double[field.Length];
        foreach (var peak in peaks)
            for (var i = 0; i < field.Length; i++)
                sum[i] += peak.Evaluate(field[i]);
        return sum;
    }

    /// <summary>Splits a flat parameter vector back into peaks (same shapes and order as <paramref name="template"/>).</summary>
    private static List<Peak> Unpack(IReadOnlyList<Peak> template, double[] values, double[]? errors = null)
    {
        var peaks = new List<Peak>(template.Count);
        var offset = 0;
        foreach (var t in template)
        {
            var n = t.Parameters.Length;
            peaks.Add(t with { Parameters = values[offset..(offset + n)], Errors = errors?[offset..(offset + n)] });
            offset += n;
        }
        return peaks;
    }
}
