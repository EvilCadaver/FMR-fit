namespace FmrFit.Core.IO;

/// <summary>One peak parameter as stored in a fit file.</summary>
/// <param name="Error">Standard error from the last fit; null if fixed or not fitted.</param>
/// <param name="Unit">Plain-ASCII unit, e.g. "Oe" or "mV*Oe^2".</param>
/// <param name="Min">Lower limit (may be −∞).</param>
/// <param name="Max">Upper limit (may be +∞).</param>
/// <param name="Fixed">True if the fit leaves the parameter unchanged.</param>
public sealed record FitFileParameter(string Name, double Value, double? Error, string Unit, double Min, double Max, bool Fixed);

/// <summary>The model of one peak shape, written once per shape used.</summary>
/// <param name="Shape">Shape identifier, e.g. "LorentzianDerivative".</param>
/// <param name="Formula">Plain-ASCII formula, e.g. "f(H) = ..., with u = H - xc".</param>
public sealed record FitFileFormula(string Shape, string Formula);

/// <summary>One peak as stored in a fit file.</summary>
/// <param name="Shape">Shape identifier, e.g. "LorentzianDerivative".</param>
/// <param name="KeepHeight">Whether manual width changes scale the amplitudes.</param>
public sealed record FitFilePeak(string Shape, bool KeepHeight, IReadOnlyList<FitFileParameter> Parameters);

/// <summary>
/// A saved peak fit of one work file (plain data; see <see cref="FitFileWriter"/> for the format). The statistics use
/// weight 1 for every point: χ² is the residual sum of squares in mV², reduced χ² = χ²/(N − p). When
/// <see cref="Data"/> is the integral or derivative, "mV" in the statistics stands for that column's unit
/// (mV·Oe or mV/Oe); the property names keep the signal's units.
/// </summary>
/// <param name="WorkFile">File name of the fitted work file (same folder).</param>
/// <param name="ChiSquaredMv2">χ² = Σ (data − fit)², in mV².</param>
/// <param name="ReducedChiSquaredMv2">χ² / (N − p), in mV²; NaN if N ≤ p.</param>
/// <param name="RSquared">R² = 1 − χ² / Σ (data − mean)²; NaN for constant data.</param>
/// <param name="StandardDeviationMv">σ = √(reduced χ²), in mV.</param>
/// <param name="Points">N, number of data points.</param>
/// <param name="FreeParameters">p, number of parameters the fit may change.</param>
/// <param name="Iterations">Optimizer iterations of the last fit; null if the peaks changed after it.</param>
/// <param name="ExitReason">Why the optimizer stopped; null if the peaks changed after the last fit.</param>
/// <param name="Saved">When the file was written.</param>
/// <param name="Formulas">Formula of each peak shape used, once per shape, in order of first use.</param>
/// <param name="RangeMinOe">Lower end of the fitted field range; null for the whole spectrum (also in older files).</param>
/// <param name="RangeMaxOe">Upper end of the fitted field range; null for the whole spectrum.</param>
/// <param name="Data">Work file column that was fitted: "signal", "integral" or "derivative"; "signal" in older files.</param>
public sealed record FitFile(
    string WorkFile,
    double ChiSquaredMv2,
    double ReducedChiSquaredMv2,
    double RSquared,
    double StandardDeviationMv,
    int Points,
    int FreeParameters,
    int? Iterations,
    string? ExitReason,
    DateTimeOffset Saved,
    IReadOnlyList<FitFileFormula> Formulas,
    IReadOnlyList<FitFilePeak> Peaks,
    double? RangeMinOe = null,
    double? RangeMaxOe = null,
    string Data = "signal");
