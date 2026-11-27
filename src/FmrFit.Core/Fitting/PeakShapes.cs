namespace FmrFit.Core.Fitting;

/// <summary>What a peak parameter measures; used for units and for scaling during the fit.</summary>
public enum ParameterKind
{
    /// <summary>A field value, e.g. the resonance field (Oe).</summary>
    Position,

    /// <summary>A field width (Oe).</summary>
    Width,

    /// <summary>A signal amplitude (same units as the data).</summary>
    Amplitude,

    /// <summary>A signal amplitude times a field squared (data units · Oe²).</summary>
    AmplitudeFieldSquared,

    /// <summary>A signal change per field (data units / Oe), e.g. a background slope.</summary>
    Slope,

    /// <summary>A signal amplitude times a field (data units · Oe), e.g. the Lorentzian's A1, A2.</summary>
    AmplitudeField,
}

/// <summary>
/// Units of peak parameters, for display and for files. The data are in mV·Oe^<c>dataFieldPower</c>: 0 for the signal
/// (mV), 1 for its integral over field (mV·Oe), −1 for its derivative (mV/Oe).
/// </summary>
public static class ParameterUnits
{
    // No optional parameters: kinds.Select(Of) would bind to LINQ's (item, index) overload and pass the index as power.

    /// <summary>Unit for display with signal data (mV), e.g. "mV·Oe²".</summary>
    public static string Of(ParameterKind kind) => OfData(kind, 0);

    /// <summary>Unit in plain ASCII with signal data (mV), for files read by other programs, e.g. "mV*Oe^2".</summary>
    public static string Ascii(ParameterKind kind) => AsciiOfData(kind, 0);

    /// <summary>Unit for display when the data are in mV·Oe^<paramref name="dataFieldPower"/>.</summary>
    public static string OfData(ParameterKind kind, int dataFieldPower) =>
        FieldPower(kind) is { } power ? Format(dataFieldPower + power, ascii: false) : "Oe";

    /// <summary>Plain-ASCII unit when the data are in mV·Oe^<paramref name="dataFieldPower"/>.</summary>
    public static string AsciiOfData(ParameterKind kind, int dataFieldPower) =>
        FieldPower(kind) is { } power ? Format(dataFieldPower + power, ascii: true) : "Oe";

    /// <summary>Unit of the data themselves, e.g. "mV" or "mV·Oe".</summary>
    public static string Data(int dataFieldPower, bool ascii = false) => Format(dataFieldPower, ascii);

    /// <summary>Unit of the squared data (χ²), e.g. "mV²" or "(mV·Oe)²".</summary>
    public static string DataSquared(int dataFieldPower) =>
        dataFieldPower == 0 ? "mV²" : $"({Format(dataFieldPower, ascii: false)})²";

    /// <summary>Power of Oe the kind adds to the data unit; null for field quantities (always Oe).</summary>
    private static int? FieldPower(ParameterKind kind) => kind switch
    {
        ParameterKind.Amplitude => 0,
        ParameterKind.AmplitudeField => 1,
        ParameterKind.AmplitudeFieldSquared => 2,
        ParameterKind.Slope => -1,
        _ => null,
    };

    /// <summary>mV·Oe^power: "mV", "mV·Oe", "mV·Oe²", "mV/Oe", "mV/Oe²" (ASCII "mV*Oe^2", "mV/Oe^2").</summary>
    private static string Format(int power, bool ascii)
    {
        if (power == 0)
            return "mV";
        var n = Math.Abs(power);
        var exponent = n == 1 ? "" : ascii ? "^" + n : Superscript(n);
        return "mV" + (power > 0 ? ascii ? "*" : "·" : "/") + "Oe" + exponent;
    }

    private static string Superscript(int n) =>
        string.Concat(n.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(c => "⁰¹²³⁴⁵⁶⁷⁸⁹"[c - '0']));
}

/// <summary>Characteristic fields of one peak, for drawing: its centre and its left and right width marks.</summary>
public readonly record struct PeakMarkers(double Left, double Center, double Right);

/// <summary>A line shape that can be fitted as one peak (or a background, fitted the same way).</summary>
public interface IPeakShape
{
    /// <summary>Stable identifier, stored in fit files; never changes once released.</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>
    /// The model as one line of plain-ASCII math using <see cref="ParameterNames"/> and the field H, for fit files;
    /// e.g. "f(H) = -A*u*exp(1/2 - u^2/2), u = ...".
    /// </summary>
    string Formula { get; }

    IReadOnlyList<string> ParameterNames { get; }

    IReadOnlyList<ParameterKind> ParameterKinds { get; }

    double Evaluate(double field, ReadOnlySpan<double> parameters);

    /// <summary>Starting parameters for a new peak placed on the largest feature of <paramref name="residual"/>.</summary>
    double[] Guess(double[] field, double[] residual);

    /// <summary>
    /// Limits for a new peak with the guessed <paramref name="parameters"/>, if the shape knows better than
    /// <see cref="PeakLimits.Default"/> (which scales the guess); null to use the default.
    /// </summary>
    ParameterRange[]? GuessLimits(double[] field, double[] residual, double[] parameters) => null;

    /// <summary>Centre and the fields of the two extrema (the left and right widths); null if the shape has none.</summary>
    PeakMarkers? Markers(ReadOnlySpan<double> parameters);
}

/// <summary>The peak shapes offered for fitting; the first is the default.</summary>
public static class PeakShapes
{
    public static IReadOnlyList<IPeakShape> All { get; } =
    [
        new LorentzianDerivative(), new LorentzianAbsorption(),
        new AsymmetricGaussianDerivative(), new AsymmetricGaussian(),
        new LinearBackground(),
    ];

    /// <summary>The shape with <see cref="IPeakShape.Id"/> <paramref name="id"/>, or null if there is none.</summary>
    public static IPeakShape? ById(string id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// Shape to offer first for data that are the <paramref name="quantityOrder"/>-th field derivative of the absorbed
    /// power (see <c>MeasuredQuantity</c>): the Lorentzian derivative for dP/dH and higher derivatives, the Lorentzian
    /// absorption for P (and its integral).
    /// </summary>
    public static IPeakShape DefaultFor(int quantityOrder) =>
        quantityOrder >= 1 ? All.OfType<LorentzianDerivative>().First() : All.OfType<LorentzianAbsorption>().First();

    /// <summary>
    /// Index of the largest |residual| and the fields on either side where the residual has fallen to half of it
    /// (linearly interpolated); a side that never falls to half ends at the data edge.
    /// </summary>
    internal static (int Peak, double Left, double Right) HalfMaximum(double[] field, double[] residual)
    {
        var peak = 0;
        for (var i = 1; i < residual.Length; i++)
            if (Math.Abs(residual[i]) > Math.Abs(residual[peak]))
                peak = i;
        var half = residual[peak] / 2;
        // Residual relative to the half height, positive inside the peak.
        double Inside(int i) => (residual[i] - half) * Math.Sign(residual[peak]);

        double Edge(int step)
        {
            var i = peak;
            while (i + step >= 0 && i + step < residual.Length && Inside(i + step) > 0)
                i += step;
            var j = i + step;
            if (j < 0 || j >= residual.Length || Inside(i) - Inside(j) <= 0) // edge, or a flat (all-zero) residual
                return field[i];
            var t = Inside(i) / (Inside(i) - Inside(j));
            return field[i] + t * (field[j] - field[i]);
        }

        return (peak, Edge(-1), Edge(1));
    }

    /// <summary>Average point spacing: a floor for guessed widths.</summary>
    internal static double MinStep(double[] field) =>
        field.Length > 1 ? Math.Abs(field[^1] - field[0]) / (field.Length - 1) : 1;
}

/// <summary>
/// Field derivative of a Lorentzian line with absorption and dispersion parts (u = H − xc):
/// <c>f(H) = −A1 · 4w·u / (4u² + w²)² − A2 · (w² − 4u²) / (4u² + w²)²</c>.
/// The first term is (A1/2)·dL/du of the absorption L = w/(4u² + w²) (FWHM w); the second is −A2·dD/du of the
/// dispersion D = u/(4u² + w²). For pure absorption the extrema are ±0.6495·A1/w² at xc ∓ w/(2√3), so the
/// peak-to-peak width is w/√3. A1 &gt; 0 (positive lobe on the low-field side) is the usual sign for FMR,
/// A1 &lt; 0 for FMAR.
/// Parameters: [xc, w, A1, A2] (A1, A2 in data units · Oe²); w is used as an absolute value.
/// For peak k these are xc_k, w_k, A_k1, A_k2.
/// </summary>
public sealed class LorentzianDerivative : IPeakShape
{
    /// <summary>|absorption term| at its extrema, times w²/A1: 9/(8√3).</summary>
    public static readonly double ExtremumFactor = 9 / (8 * Math.Sqrt(3));

    public string Id => "LorentzianDerivative";

    public string Name => "Lorentzian derivative (absorption + dispersion)";

    public string Formula =>
        "f(H) = -A1*4*w*u/(4*u^2 + w^2)^2 - A2*(w^2 - 4*u^2)/(4*u^2 + w^2)^2, with u = H - xc";

    public IReadOnlyList<string> ParameterNames { get; } = ["xc", "w", "A1", "A2"];

    public IReadOnlyList<ParameterKind> ParameterKinds { get; } =
        [ParameterKind.Position, ParameterKind.Width, ParameterKind.AmplitudeFieldSquared, ParameterKind.AmplitudeFieldSquared];

    public double Evaluate(double field, ReadOnlySpan<double> p)
    {
        var w = Math.Abs(p[1]);
        if (w == 0)
            return 0;
        var u = field - p[0];
        var d = 4 * u * u + w * w;
        return (-p[2] * 4 * w * u - p[3] * (w * w - 4 * u * u)) / (d * d);
    }

    /// <summary>
    /// Pure absorption guess: centre between the residual's maximum and minimum, w = √3 × their field separation,
    /// A1 from the peak-to-peak height (positive when the maximum is at the lower field, as for FMR), A2 = 0.
    /// </summary>
    public double[] Guess(double[] field, double[] residual)
    {
        var iMax = 0;
        var iMin = 0;
        for (var i = 1; i < residual.Length; i++)
        {
            if (residual[i] > residual[iMax]) iMax = i;
            if (residual[i] < residual[iMin]) iMin = i;
        }

        var center = (field[iMax] + field[iMin]) / 2;
        var minStep = field.Length > 1 ? Math.Abs(field[^1] - field[0]) / (field.Length - 1) : 1;
        var w = Math.Sqrt(3) * Math.Max(Math.Abs(field[iMin] - field[iMax]), minStep);
        var halfHeight = (residual[iMax] - residual[iMin]) / 2;
        var a1 = halfHeight * w * w / ExtremumFactor * (field[iMax] <= field[iMin] ? 1 : -1);
        return [center, w, a1, 0];
    }

    /// <summary>Centre xc and the absorption extrema xc ∓ w/(2√3).</summary>
    public PeakMarkers? Markers(ReadOnlySpan<double> p)
    {
        var half = Math.Abs(p[1]) / (2 * Math.Sqrt(3));
        return new PeakMarkers(p[0] - half, p[0], p[0] + half);
    }
}

/// <summary>
/// Lorentzian line with absorption and dispersion parts (u = H − xc), the field integral of
/// <see cref="LorentzianDerivative"/> with the same parameters:
/// <c>f(H) = A1 · w / (2(4u² + w²)) − A2 · u / (4u² + w²)</c>.
/// The absorption term peaks at xc with height A1/(2w) and full width at half maximum w; the dispersion term is
/// antisymmetric with extrema ∓A2/(4w) at xc ∓ w/2. A1 &gt; 0 (an absorption peak) is the usual sign for FMR, A1 &lt; 0
/// for FMAR, as for the derivative. Parameters: [xc, w, A1, A2] (A1, A2 in data units · Oe); w is used as an absolute
/// value.
/// </summary>
public sealed class LorentzianAbsorption : IPeakShape
{
    public string Id => "LorentzianAbsorption";

    public string Name => "Lorentzian (absorption + dispersion)";

    public string Formula => "f(H) = A1*w/(2*(4*u^2 + w^2)) - A2*u/(4*u^2 + w^2), with u = H - xc";

    public IReadOnlyList<string> ParameterNames { get; } = ["xc", "w", "A1", "A2"];

    public IReadOnlyList<ParameterKind> ParameterKinds { get; } =
        [ParameterKind.Position, ParameterKind.Width, ParameterKind.AmplitudeField, ParameterKind.AmplitudeField];

    public double Evaluate(double field, ReadOnlySpan<double> p)
    {
        var w = Math.Abs(p[1]);
        if (w == 0)
            return 0;
        var u = field - p[0];
        return (p[2] * w / 2 - p[3] * u) / (4 * u * u + w * w);
    }

    /// <summary>
    /// Pure absorption guess on the largest |residual|: centre there, w its full width at half maximum, A1 = 2w × its
    /// height (negative for a dip), A2 = 0.
    /// </summary>
    public double[] Guess(double[] field, double[] residual)
    {
        var (peak, left, right) = PeakShapes.HalfMaximum(field, residual);
        var w = Math.Max(Math.Abs(right - left), 2 * PeakShapes.MinStep(field));
        return [field[peak], w, 2 * w * residual[peak], 0];
    }

    /// <summary>
    /// Centre xc and the inflection points xc ∓ w/(2√3): the same marks as <see cref="LorentzianDerivative"/>, where
    /// the derivative has its extrema.
    /// </summary>
    public PeakMarkers? Markers(ReadOnlySpan<double> p)
    {
        var half = Math.Abs(p[1]) / (2 * Math.Sqrt(3));
        return new PeakMarkers(p[0] - half, p[0], p[0] + half);
    }
}

/// <summary>
/// Gaussian with different widths below and above the resonance field:
/// <c>f(H) = A · exp(−u²/2)</c>, <c>u = (H − H0)/σ</c>, with σ = σL for H &lt; H0 and σ = σR otherwise.
/// The height is A at H0; the inflection points are at H0 − σL and H0 + σR (where
/// <see cref="AsymmetricGaussianDerivative"/> has its extrema), and the full width at half maximum is
/// √(2 ln 2)·(σL + σR). Parameters: [H0, A, σL, σR]; the widths are used as absolute values.
/// </summary>
public sealed class AsymmetricGaussian : IPeakShape
{
    private static readonly double HalfWidthPerSigma = Math.Sqrt(2 * Math.Log(2));

    public string Id => "AsymmetricGaussian";

    public string Name => "Asymmetric Gaussian";

    public string Formula =>
        "f(H) = A*exp(-u^2/2), with u = (H - H0)/s, s = σL for H < H0 and s = σR otherwise";

    public IReadOnlyList<string> ParameterNames { get; } = ["H0", "A", "σL", "σR"];

    public IReadOnlyList<ParameterKind> ParameterKinds { get; } =
        [ParameterKind.Position, ParameterKind.Amplitude, ParameterKind.Width, ParameterKind.Width];

    public double Evaluate(double field, ReadOnlySpan<double> p)
    {
        var center = p[0];
        var sigma = Math.Abs(field < center ? p[2] : p[3]);
        if (sigma == 0)
            return 0;
        var u = (field - center) / sigma;
        return p[1] * Math.Exp(-0.5 * u * u);
    }

    /// <summary>
    /// On the largest |residual|: centre there, A its height (negative for a dip), σL and σR from the half-maximum
    /// points on each side.
    /// </summary>
    public double[] Guess(double[] field, double[] residual)
    {
        var (peak, left, right) = PeakShapes.HalfMaximum(field, residual);
        var minWidth = PeakShapes.MinStep(field);
        var center = field[peak];
        return
        [
            center, residual[peak],
            Math.Max(Math.Abs(center - left) / HalfWidthPerSigma, minWidth),
            Math.Max(Math.Abs(right - center) / HalfWidthPerSigma, minWidth),
        ];
    }

    /// <summary>Centre H0 and the inflection points H0 − σL and H0 + σR.</summary>
    public PeakMarkers? Markers(ReadOnlySpan<double> p) => new PeakMarkers(p[0] - Math.Abs(p[2]), p[0], p[0] + Math.Abs(p[3]));
}

/// <summary>
/// Derivative of a Gaussian with different widths below and above the resonance field:
/// <c>f(H) = −A · u · exp(½ − u²/2)</c>, <c>u = (H − H0)/σ</c>, with σ = σL for H &lt; H0 and σ = σR otherwise.
/// The extrema are +A at H0 − σL and −A at H0 + σR, so A is half the peak-to-peak height and σL + σR is the
/// peak-to-peak width. Parameters: [H0, A, σL, σR]; the widths are used as absolute values.
/// </summary>
public sealed class AsymmetricGaussianDerivative : IPeakShape
{
    public string Id => "AsymmetricGaussianDerivative";

    public string Name => "Asymmetric Gaussian derivative";

    public string Formula =>
        "f(H) = -A*u*exp(1/2 - u^2/2), with u = (H - H0)/s, s = σL for H < H0 and s = σR otherwise";

    public IReadOnlyList<string> ParameterNames { get; } = ["H0", "A", "σL", "σR"];

    public IReadOnlyList<ParameterKind> ParameterKinds { get; } =
        [ParameterKind.Position, ParameterKind.Amplitude, ParameterKind.Width, ParameterKind.Width];

    public double Evaluate(double field, ReadOnlySpan<double> p)
    {
        var center = p[0];
        var sigma = Math.Abs(field < center ? p[2] : p[3]);
        if (sigma == 0)
            return 0;
        var u = (field - center) / sigma;
        return -p[1] * u * Math.Exp(0.5 - 0.5 * u * u);
    }

    /// <summary>
    /// Centre between the residual's maximum and minimum, amplitude half their difference (positive when the
    /// maximum is at the lower field), both widths half their field separation.
    /// </summary>
    public double[] Guess(double[] field, double[] residual)
    {
        var iMax = 0;
        var iMin = 0;
        for (var i = 1; i < residual.Length; i++)
        {
            if (residual[i] > residual[iMax]) iMax = i;
            if (residual[i] < residual[iMin]) iMin = i;
        }

        var center = (field[iMax] + field[iMin]) / 2;
        var amplitude = (residual[iMax] - residual[iMin]) / 2 * (field[iMax] <= field[iMin] ? 1 : -1);
        var minWidth = field.Length > 1 ? Math.Abs(field[^1] - field[0]) / (field.Length - 1) : 1;
        var width = Math.Max(Math.Abs(field[iMin] - field[iMax]) / 2, minWidth);
        return [center, amplitude, width, width];
    }

    /// <summary>Centre H0 and the extrema H0 − σL and H0 + σR.</summary>
    public PeakMarkers? Markers(ReadOnlySpan<double> p) => new PeakMarkers(p[0] - Math.Abs(p[2]), p[0], p[0] + Math.Abs(p[3]));
}

/// <summary>
/// Linear background <c>f(H) = c + s·H</c>: offset c (data units, the value at H = 0) and slope s (data units / Oe).
/// Added and fitted like a peak, e.g. for a baseline that is offset or tilted within the fit range.
/// </summary>
public sealed class LinearBackground : IPeakShape
{
    /// <summary>Default limits: this many times the largest |residual| (offset), or that over the field span (slope).</summary>
    public const double LimitFactor = 10;

    public string Id => "LinearBackground";

    public string Name => "Linear background";

    public string Formula => "f(H) = c + s*H";

    public IReadOnlyList<string> ParameterNames { get; } = ["c", "s"];

    public IReadOnlyList<ParameterKind> ParameterKinds { get; } = [ParameterKind.Amplitude, ParameterKind.Slope];

    public double Evaluate(double field, ReadOnlySpan<double> p) => p[0] + p[1] * field;

    /// <summary>Least-squares straight line through the residual.</summary>
    public double[] Guess(double[] field, double[] residual)
    {
        if (field.Length == 0)
            return [0, 0];
        var meanH = field.Average();
        var meanR = residual.Average();
        double sxy = 0, sxx = 0;
        for (var i = 0; i < field.Length; i++)
        {
            sxy += (field[i] - meanH) * (residual[i] - meanR);
            sxx += (field[i] - meanH) * (field[i] - meanH);
        }
        var slope = sxx > 0 ? sxy / sxx : 0;
        return [meanR - slope * meanH, slope];
    }

    /// <summary>
    /// Offset within ±(<see cref="LimitFactor"/> × largest |residual| + 2 |c|), slope within
    /// ±(<see cref="LimitFactor"/> × largest |residual| / field span + 2 |s|): set by the data, not by a guess that
    /// may be close to 0.
    /// </summary>
    public ParameterRange[]? GuessLimits(double[] field, double[] residual, double[] parameters)
    {
        var scale = residual.Length > 0 ? residual.Max(Math.Abs) : 0;
        var span = field.Length > 1 ? field.Max() - field.Min() : 0;
        var offset = LimitFactor * scale + 2 * Math.Abs(parameters[0]);
        var slope = (span > 0 ? LimitFactor * scale / span : 0) + 2 * Math.Abs(parameters[1]);
        return
        [
            PeakLimits.FromLimit(ParameterKind.Amplitude, offset > 0 ? offset : 1),
            PeakLimits.FromLimit(ParameterKind.Slope, slope > 0 ? slope : 1),
        ];
    }

    public PeakMarkers? Markers(ReadOnlySpan<double> parameters) => null;
}
