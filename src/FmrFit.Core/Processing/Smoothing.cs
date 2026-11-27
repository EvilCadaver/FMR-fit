using System.Globalization;
using Complex = System.Numerics.Complex;
using FmrFit.Core.IO;
using MathNet.Numerics.IntegralTransforms;
using MathNet.Numerics.LinearAlgebra;

namespace FmrFit.Core.Processing;

public enum SmoothingMethod
{
    None,
    Fft,
    SavitzkyGolay,
}

/// <summary>Smoothing of the interpolated signal, see <see cref="Smoothing.Apply"/>.</summary>
/// <param name="Points">Span in points: the cutoff period of <see cref="SmoothingMethod.Fft"/>, the window of
/// <see cref="SmoothingMethod.SavitzkyGolay"/> (odd).</param>
/// <param name="PolynomialOrder">Order of the Savitzky-Golay polynomial (1 linear, 2 quadratic, …); smaller than
/// <paramref name="Points"/>.</param>
public sealed record SmoothingSettings(SmoothingMethod Method, int Points, int PolynomialOrder)
{
    public const int DefaultFftPoints = 3;
    public const int DefaultSavitzkyGolayPoints = 5;
    public const int DefaultPolynomialOrder = 2;
    public const int MaxPoints = 999;
    public const int MaxPolynomialOrder = 6;

    public static SmoothingSettings None { get; } = new(SmoothingMethod.None, 0, 0);

    /// <summary>The method with its default span and polynomial order.</summary>
    public static SmoothingSettings Default(SmoothingMethod method) => method switch
    {
        SmoothingMethod.Fft => new(method, DefaultFftPoints, DefaultPolynomialOrder),
        SmoothingMethod.SavitzkyGolay => new(method, DefaultSavitzkyGolayPoints, DefaultPolynomialOrder),
        _ => None,
    };

    /// <summary>The smoothing recorded in a work file; missing values are the method's defaults.</summary>
    public static SmoothingSettings FromWorkFile(WorkFileProcessing p)
    {
        var defaults = Default(SmoothingMethodText.Parse(p.Smoothing));
        if (defaults.Method == SmoothingMethod.None)
            return None;
        return new SmoothingSettings(defaults.Method, p.SmoothingPoints ?? defaults.Points,
            p.SmoothingPolynomialOrder ?? defaults.PolynomialOrder).Coerce();
    }

    /// <summary>Smallest valid span: FFT 2 (no smoothing), Savitzky-Golay the smallest odd number above the order.</summary>
    public int MinPoints => Method == SmoothingMethod.SavitzkyGolay ? Odd(PolynomialOrder + 1) : 2;

    /// <summary>
    /// The nearest valid settings: order within [1, <see cref="MaxPolynomialOrder"/>], span within
    /// [<see cref="MinPoints"/>, <see cref="MaxPoints"/>], rounded up to odd for Savitzky-Golay.
    /// </summary>
    public SmoothingSettings Coerce()
    {
        if (Method == SmoothingMethod.None)
            return None;
        var order = Math.Clamp(PolynomialOrder, 1, MaxPolynomialOrder);
        var coerced = this with { PolynomialOrder = order };
        var points = Math.Clamp(Points, coerced.MinPoints, MaxPoints);
        return coerced with { Points = Method == SmoothingMethod.SavitzkyGolay ? Odd(points) : points };
    }

    private static int Odd(int n) => n % 2 == 0 ? n + 1 : n;
}

/// <summary>Text form of <see cref="SmoothingMethod"/> as stored in work files.</summary>
public static class SmoothingMethodText
{
    public static string ToText(SmoothingMethod method) => method switch
    {
        SmoothingMethod.Fft => "fft",
        SmoothingMethod.SavitzkyGolay => "savitzky-golay",
        _ => "off",
    };

    /// <summary>Inverse of <see cref="ToText"/>; anything unrecognized (including a missing entry) is off.</summary>
    public static SmoothingMethod Parse(string? text) => text?.Trim().ToLower(CultureInfo.InvariantCulture) switch
    {
        "fft" => SmoothingMethod.Fft,
        "savitzky-golay" => SmoothingMethod.SavitzkyGolay,
        _ => SmoothingMethod.None,
    };
}

/// <summary>Smoothing of equally spaced values (the interpolated signal). Both methods keep the number of points.</summary>
public static class Smoothing
{
    /// <summary>
    /// Smooths <paramref name="values"/> with <paramref name="settings"/> (coerced to valid settings first); returns a
    /// copy unchanged for <see cref="SmoothingMethod.None"/>.
    /// </summary>
    public static double[] Apply(double[] values, SmoothingSettings settings)
    {
        settings = settings.Coerce();
        return settings.Method switch
        {
            SmoothingMethod.Fft => FftLowPass(values, settings.Points),
            SmoothingMethod.SavitzkyGolay => SavitzkyGolay(values, settings.Points, settings.PolynomialOrder),
            _ => (double[])values.Clone(),
        };
    }

    /// <summary>
    /// FFT low-pass: removes the Fourier components with a period shorter than <paramref name="points"/> points
    /// (frequency above 1/(points·Δ)). The data are mirrored before the transform, so the periodic continuation has
    /// no jump at the ends. 2 points (the Nyquist frequency) leaves the data unchanged.
    /// </summary>
    public static double[] FftLowPass(double[] values, int points)
    {
        if (points < 2)
            throw new ArgumentOutOfRangeException(nameof(points), points, "The FFT smoothing span must be at least 2 points.");
        var n = values.Length;
        if (n < 3)
            return (double[])values.Clone();

        // Even extension v0 … v(n-1) v(n-2) … v1: period 2n − 2, continuous across both ends.
        var m = 2 * n - 2;
        var spectrum = new Complex[m];
        for (var i = 0; i < n; i++)
            spectrum[i] = values[i];
        for (var i = 1; i < n - 1; i++)
            spectrum[m - i] = values[i];

        Fourier.Forward(spectrum, FourierOptions.NoScaling);
        // Bin k has frequency min(k, m − k)/m per point; keep those up to 1/points.
        for (var k = 0; k < m; k++)
            if (Math.Min(k, m - k) * (double)points > m)
                spectrum[k] = Complex.Zero;
        Fourier.Inverse(spectrum, FourierOptions.NoScaling);

        var result = new double[n];
        for (var i = 0; i < n; i++)
            result[i] = spectrum[i].Real / m;
        return result;
    }

    /// <summary>
    /// Savitzky-Golay: each value is replaced by a least-squares polynomial of <paramref name="order"/> through the
    /// <paramref name="points"/> values centred on it. Near the ends, where the window would run off the data, the
    /// polynomial through the first (last) <paramref name="points"/> values is evaluated at each end point. With fewer
    /// values than the window the data are returned unchanged.
    /// </summary>
    public static double[] SavitzkyGolay(double[] values, int points, int order)
    {
        if (points < 3 || points % 2 == 0)
            throw new ArgumentOutOfRangeException(nameof(points), points, "The Savitzky-Golay window must be an odd number of at least 3 points.");
        if (order < 0 || order >= points)
            throw new ArgumentOutOfRangeException(nameof(order), order, "The polynomial order must be at least 0 and below the window.");
        var n = values.Length;
        if (n < points)
            return (double[])values.Clone();

        var half = points / 2;
        var weights = SavitzkyGolayWeights(points, order);
        var result = new double[n];
        for (var i = 0; i < n; i++)
        {
            // Window start, kept inside the data; the point sits at offset i − start within it.
            var start = Math.Clamp(i - half, 0, n - points);
            var w = weights[i - start];
            double sum = 0;
            for (var j = 0; j < points; j++)
                sum += w[j] * values[start + j];
            result[i] = sum;
        }
        return result;
    }

    /// <summary>
    /// weights[p][j]: contribution of window value j to the fitted polynomial evaluated at window position p.
    /// Positions are scaled to [−1, 1] to keep the least-squares problem well conditioned.
    /// </summary>
    private static double[][] SavitzkyGolayWeights(int points, int order)
    {
        var half = points / 2;
        double Position(int j) => (j - half) / (double)half;

        var design = Matrix<double>.Build.Dense(points, order + 1, (j, k) => Math.Pow(Position(j), k));
        // Rows of the pseudo-inverse give the polynomial coefficients as combinations of the window values.
        var pseudoInverse = design.PseudoInverse();
        var weights = new double[points][];
        for (var p = 0; p < points; p++)
        {
            var powers = Vector<double>.Build.Dense(order + 1, k => Math.Pow(Position(p), k));
            weights[p] = pseudoInverse.LeftMultiply(powers).ToArray();
        }
        return weights;
    }
}
