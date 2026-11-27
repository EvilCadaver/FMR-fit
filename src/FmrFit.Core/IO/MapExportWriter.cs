using System.Globalization;
using System.Text;

namespace FmrFit.Core.IO;

/// <summary>
/// Writes a <see cref="MapExport"/> as a CSV for 3D plots: a few "#" comment lines, then one row per point.
/// <code>
/// # FMR-fit 3D export, format 1
/// # sample = MM100610_cprllH
/// # frequency_from_to_step_GHz = 30; 39; 1      &lt;- step empty for a single frequency
/// # field_step_Oe = 10                          &lt;- "; "-separated if the work files differ
/// # spectra = 10
/// # quantity = P                                &lt;- what the values stand for: ∫P dH, P, dP/dH or d²P/dH²
/// # data = dP/dH integral (4); ~P signal (6)    &lt;- recorded signal and displayed column of the work files
/// # align = ...10 points...                     &lt;- or "off"
/// # shift = ...10 points...                     &lt;- or "off"
/// # normalise = ...                             &lt;- or "off"
/// # spectrum_operations = f_GHz; baseline_at_0_Oe; baseline_slope_per_Oe; divisor (value = ...)
/// # spectrum = 30; -0.0123; 1.2E-06; 4.567      &lt;- one per spectrum, only if aligned, shifted or normalised
/// f_GHz,H_Oe,P                                  &lt;- quantity, with its unit (e.g. P_mV) if all files share one;
/// 30,230,-5.0123                                   unit 1 (e.g. P_1) if normalised
/// </code>
/// Numbers use invariant culture. Header numbers use round-trip formatting, as in work files; data values are rounded
/// to a chosen number of decimal places (trailing zeros dropped, never in exponent form), or round-trip if none is given.
/// </summary>
public static class MapExportWriter
{
    public const string Signature = "# FMR-fit 3D export";
    public const int FormatVersion = 1;
    public const string DataHeaderStart = "f_GHz,H_Oe,";
    public const string Extension = ".csv";

    /// <summary>Default and most decimal places of the data values (the most <see cref="Math.Round(double, int)"/> allows).</summary>
    public const int DefaultDecimals = 6, MaxDecimals = 15;

    /// <summary>Suffix of the suggested file name: "[sample]_3D.csv".</summary>
    public const string NameSuffix = "_3D";

    /// <summary>Writes through a temporary file, so an existing file is only replaced once the new one is complete.</summary>
    /// <param name="decimals">Decimal places of the data values (0…<see cref="MaxDecimals"/>); null = full precision.</param>
    public static void Write(string path, MapExport map, int? decimals = DefaultDecimals)
    {
        var temp = path + ".tmp";
        try
        {
            using (var writer = new StreamWriter(temp, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                Write(writer, map, decimals);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <inheritdoc cref="Write(string, MapExport, int?)"/>
    public static void Write(TextWriter writer, MapExport map, int? decimals = DefaultDecimals)
    {
        var n = map.FrequencyGHz.Length;
        if (map.FieldOe.Length != n || map.Values.Length != n)
            throw new ArgumentException("Frequency, field and values must have the same length.");
        if (decimals is < 0 or > MaxDecimals)
            throw new ArgumentOutOfRangeException(nameof(decimals), decimals, $"Decimal places must be 0 to {MaxDecimals}.");
        Func<double, string> value = decimals is { } d ? v => Rounded(v, d) : WorkFileWriter.Format;

        writer.NewLine = "\n";
        writer.WriteLine($"{Signature}, format {FormatVersion}");
        writer.WriteLine($"# sample = {WorkFileWriter.Escape(string.Join(WorkFileWriter.ListSeparator, map.SampleNames))}");
        writer.WriteLine("# frequency_from_to_step_GHz = " + string.Join(WorkFileWriter.ListSeparator,
            WorkFileWriter.Format(map.FrequencyFromGHz),
            WorkFileWriter.Format(map.FrequencyToGHz),
            map.FrequencyStepGHz is { } step ? WorkFileWriter.Format(step) : ""));
        writer.WriteLine($"# field_step_Oe = {string.Join(WorkFileWriter.ListSeparator, map.FieldStepsOe.Select(WorkFileWriter.Format))}");
        writer.WriteLine($"# spectra = {map.Spectra}");
        writer.WriteLine($"# quantity = {map.Quantity}");
        writer.WriteLine($"# data = {map.Data}");
        writer.WriteLine("# align = " + (map.AlignmentPoints is { } points
            ? $"the straight line through the means of the first and the last {points} points of each spectrum is subtracted"
            : "off"));
        writer.WriteLine("# shift = " + (map.ShiftPoints is { } shiftPoints
            ? $"the mean of the last {shiftPoints} points of each spectrum is subtracted (after aligning)"
            : "off"));
        writer.WriteLine("# normalise = " + (map.Normalised
            ? "each spectrum is divided by its largest |value| (after aligning and shifting); the values have unit 1"
            : "off"));
        var operations = map.Operations ?? [];
        if (operations.Count > 0)
        {
            writer.WriteLine("# spectrum_operations = f_GHz; baseline_at_0_Oe; baseline_slope_per_Oe; divisor " +
                             "(value = (original - baseline_at_0_Oe - baseline_slope_per_Oe*H) / divisor; " +
                             "baseline in the original unit; empty = not done)");
            foreach (var o in operations)
                writer.WriteLine("# spectrum = " + string.Join(WorkFileWriter.ListSeparator,
                    WorkFileWriter.Format(o.FrequencyGHz), Optional(o.BaselineIntercept), Optional(o.BaselineSlope),
                    Optional(o.Divisor)));
        }

        writer.WriteLine(DataHeaderStart + map.ValueColumn);
        for (var i = 0; i < n; i++)
            writer.WriteLine(
                $"{value(map.FrequencyGHz[i])},{value(map.FieldOe[i])},{value(map.Values[i])}");
    }

    private static string Optional(double? value) => value is { } v ? WorkFileWriter.Format(v) : "";

    /// <summary>
    /// <paramref name="value"/> rounded to <paramref name="decimals"/> places (halves away from zero), without trailing
    /// zeros or exponent: 1.23456789 → "1.234568", 230 → "230", -1E-7 → "0".
    /// </summary>
    public static string Rounded(double value, int decimals)
    {
        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        if (rounded == 0)
            rounded = 0; // no "-0"
        var format = decimals == 0 ? "0" : "0." + new string('#', decimals);
        return rounded.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>Suggested file name without extension: the most frequent sample name + <see cref="NameSuffix"/>.</summary>
    public static string SuggestName(IEnumerable<WorkFile> workFiles) => ProjectArchive.SuggestName(workFiles) + NameSuffix;
}
