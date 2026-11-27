namespace FmrFit.Core.IO;

/// <summary>
/// Information from the lines above the column header of a measurement file.
/// </summary>
/// <param name="SampleName">Value of the "Sample Name" line, if present.</param>
/// <param name="FrequencyGHz">Microwave frequency from the "Freq" entry, if present.</param>
/// <param name="PowerDbm">Microwave power from the "Power" entry, if present.</param>
/// <param name="Comment">Free text between "dBm," and "SR850:" (may span several lines), or null if absent.</param>
/// <param name="LockInSettings">Items of the "SR850:" line, e.g. "f = 25 Hz", "tc = 300 ms"; empty if absent.</param>
/// <param name="LockInPhaseDeg">Reference phase from the "phase = 96.5 deg" lock-in setting, if present.</param>
/// <param name="PreambleLines">All raw lines above the column header, in file order.</param>
public sealed record FmrMetadata(
    string? SampleName,
    double? FrequencyGHz,
    double? PowerDbm,
    string? Comment,
    IReadOnlyList<string> LockInSettings,
    double? LockInPhaseDeg,
    IReadOnlyList<string> PreambleLines)
{
    public static FmrMetadata Empty { get; } = new(null, null, null, null, [], null, []);
}

/// <summary>
/// One field-swept FMR measurement: lock-in X/Y signal versus time and field.
/// All arrays have the same length and are ordered as recorded.
/// </summary>
/// <param name="Time">Time stamps (s), strictly increasing.</param>
/// <param name="Field">Applied field (Oe).</param>
/// <param name="X">In-phase lock-in signal (V).</param>
/// <param name="Y">Quadrature lock-in signal (V).</param>
/// <param name="SkippedRepeatedRows">Rows dropped because their time stamp repeated the previous row's.</param>
public sealed record FmrMeasurement(
    FmrMetadata Metadata,
    double[] Time,
    double[] Field,
    double[] X,
    double[] Y,
    int SkippedRepeatedRows)
{
    public int Count => Time.Length;
}
