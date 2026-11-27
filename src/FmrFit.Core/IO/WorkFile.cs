namespace FmrFit.Core.IO;

/// <summary>Where a work file's data came from.</summary>
/// <param name="FilePath">Full path of the original measurement file (or its name if no path is available).</param>
/// <param name="Imported">When the work file was created.</param>
public sealed record WorkFileSource(string? FilePath, DateTimeOffset Imported);

/// <summary>Post-processing used to produce a work file's data (plain values, so IO does not depend on Processing).</summary>
/// <param name="Sweeps">"up&amp;down", "up" or "down".</param>
/// <param name="PowerReferenceDbm">Reference power of the normalization, or null if the signal is not normalized.</param>
/// <param name="PowerFactor">Factor applied by the normalization, or null if the signal is not normalized.</param>
/// <param name="Smoothing">"off", "fft" or "savitzky-golay"; "off" for files written before smoothing existed.</param>
/// <param name="SmoothingPoints">Span of the smoothing in grid points, or null when off.</param>
/// <param name="SmoothingPolynomialOrder">Savitzky-Golay polynomial order, or null when not Savitzky-Golay.</param>
/// <param name="Display">Column the main window shows: "signal", "integral" or "derivative"; "signal" for older files.</param>
/// <param name="DisplayOffset">Offset for the displayed column, in its unit (mV, mV·Oe or mV/Oe). The stored columns
/// never include it; it is added when the column is shown, fitted or exported. 0 for older files.</param>
/// <param name="DisplayOffsetApplied">Whether <paramref name="DisplayOffset"/> is added ("offset to 0" on).</param>
public sealed record WorkFileProcessing(
    int SkippedLeadingRows,
    int SkippedRepeatedRows,
    double SweepCorrectionOe,
    double PhaseDeg,
    double PhaseChangeDeg,
    string Sweeps,
    double TimeBackgroundIntercept,
    double TimeBackgroundSlope,
    double FieldBackgroundIntercept,
    double FieldBackgroundSlope,
    double GridFromOe,
    double GridToOe,
    double GridStepOe,
    double? PowerReferenceDbm,
    double? PowerFactor,
    string Smoothing = "off",
    int? SmoothingPoints = null,
    int? SmoothingPolynomialOrder = null,
    string Display = "signal",
    double DisplayOffset = 0,
    bool DisplayOffsetApplied = false);

/// <summary>
/// A processed measurement as stored in the project folder: the original file's metadata, the processing used,
/// and the interpolated signal. See <see cref="WorkFileWriter"/> for the file format.
/// </summary>
/// <param name="SweepRateUpOePerS">Fitted field sweep rate of the up sweep, if known.</param>
/// <param name="SweepRateDownOePerS">Fitted field sweep rate of the down sweep, if known.</param>
/// <param name="FieldOe">Interpolated field grid (Oe).</param>
/// <param name="SignalMv">Processed signal (mV) at each field.</param>
/// <param name="IntegralMvOe">Integral of the signal over field (mV·Oe), or null if the file has no such column.</param>
/// <param name="DerivativeMvPerOe">Derivative of the signal over field (mV/Oe), or null if the file has no such column.</param>
/// <param name="Recorded">What the measurement recorded: "dP/dH" or "~P" (set by the user at import); "dP/dH" for
/// older files.</param>
public sealed record WorkFile(
    WorkFileSource Source,
    FmrMetadata Metadata,
    double? SweepRateUpOePerS,
    double? SweepRateDownOePerS,
    WorkFileProcessing Processing,
    double[] FieldOe,
    double[] SignalMv,
    double[]? IntegralMvOe = null,
    double[]? DerivativeMvPerOe = null,
    string Recorded = "dP/dH");
