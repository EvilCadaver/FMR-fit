namespace FmrFit.Core.IO;

/// <summary>
/// Several work files combined into one long table for 3D plotting (frequency, field, value), with the short
/// summary written above it. Built by <c>MapExportBuilder</c>, written by <see cref="MapExportWriter"/>.
/// All three columns have the same length; one row per data point.
/// </summary>
/// <param name="SampleNames">Distinct sample names, most frequent first; empty if none is known.</param>
/// <param name="FrequencyStepGHz">Most regular spacing of the frequencies; null for a single frequency.</param>
/// <param name="FieldStepsOe">Distinct interpolation steps of the field grids, ascending (usually one).</param>
/// <param name="Spectra">Number of work files in the table.</param>
/// <param name="Values">The work files' displayed columns (signal, integral or derivative), all standing for
/// <paramref name="Quantity"/>.</param>
/// <param name="Quantity">Physical quantity of the values, e.g. "P" or "dP/dH".</param>
/// <param name="ValueColumn">Header of the value column, e.g. "P_mV"; without unit if the files' units differ.</param>
/// <param name="Data">Where the values come from, e.g. "dP/dH integral (2); ~P signal (3)".</param>
/// <param name="AlignmentPoints">Points averaged at each end when the spectra were aligned; null if not aligned.</param>
/// <param name="Normalised">Whether each spectrum was divided by its largest |value| (values then have unit 1).</param>
/// <param name="Operations">What was done to each spectrum, in table order; empty if nothing was done.</param>
/// <param name="ShiftPoints">Points at the end of each spectrum whose mean was subtracted; null if not shifted.</param>
public sealed record MapExport(
    IReadOnlyList<string> SampleNames,
    double FrequencyFromGHz,
    double FrequencyToGHz,
    double? FrequencyStepGHz,
    IReadOnlyList<double> FieldStepsOe,
    int Spectra,
    double[] FrequencyGHz,
    double[] FieldOe,
    double[] Values,
    string Quantity = "dP/dH",
    string ValueColumn = "dPdH_mV",
    string Data = "dP/dH signal",
    int? AlignmentPoints = null,
    bool Normalised = false,
    IReadOnlyList<MapExportOperation>? Operations = null,
    int? ShiftPoints = null);

/// <summary>
/// Changes made to one spectrum's values on export: value = (original − (<paramref name="BaselineIntercept"/> +
/// <paramref name="BaselineSlope"/>·H)) / <paramref name="Divisor"/>, with the parts not done left null.
/// </summary>
/// <param name="BaselineIntercept">Subtracted line (and shift) at H = 0, in the original unit; null if neither
/// aligned nor shifted.</param>
/// <param name="BaselineSlope">Slope of the subtracted line, original unit per Oe; null if not aligned.</param>
/// <param name="Divisor">Largest |value| after aligning and shifting, divided by; null if not normalised.</param>
public sealed record MapExportOperation(double FrequencyGHz, double? BaselineIntercept, double? BaselineSlope, double? Divisor);
