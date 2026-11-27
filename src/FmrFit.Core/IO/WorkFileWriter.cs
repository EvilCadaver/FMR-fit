using System.Globalization;
using System.Text;

namespace FmrFit.Core.IO;

/// <summary>
/// Writes work files: a text CSV whose header lines start with "#", followed by two data columns.
/// <code>
/// # FMR-fit work file, format 1
/// # [source]
/// # file = C:\data\MM100610_cprllH_42G.csv   &lt;- full path of the measurement file
/// # [measurement]
/// # sample = MM100610_cprllH
/// # recorded_signal = dP/dH                &lt;- what was measured: dP/dH or ~P (chosen at import)
/// # comment = first line\nsecond line      &lt;- line breaks escaped as \n, backslashes as \\
/// # [original header]
/// # Sample Name, MM100610_cprllH           &lt;- preamble of the measurement file, verbatim
/// # [processing]
/// # field_background = 3.7888E-06; -5.4371E-10
/// # smoothing = savitzky-golay               &lt;- off, fft or savitzky-golay
/// # smoothing_points = 5                     &lt;- empty when off
/// # smoothing_polynomial_order = 2           &lt;- empty unless savitzky-golay
/// # display = integral                       &lt;- column the main window shows: signal, integral or derivative
/// # display_offset = 1523.7                  &lt;- offset for that column, in its unit (the data below never include it)
/// # display_offset_applied = yes             &lt;- yes: added when the column is shown, fitted or exported
/// H_Oe,V_mV,Int_mV_Oe,dVdH_mV_per_Oe
/// 230,-5.0123,0,0.0011
/// </code>
/// Header entries are "# key = value" inside "# [section]" blocks; values never contain a raw line break, and
/// multi-value entries are separated by "; ". Numbers use invariant culture and round-trip formatting.
/// Plain CSV: every "#" line is a comment, then the header row and the data. Files without the integral and
/// derivative have only the first two columns (<see cref="DataHeader"/>).
/// </summary>
public static class WorkFileWriter
{
    public const string Signature = "# FMR-fit work file";
    public const int FormatVersion = 1;
    public const string SignalColumn = "V_mV", IntegralColumn = "Int_mV_Oe", DerivativeColumn = "dVdH_mV_per_Oe";
    public const string DataHeader = "H_Oe," + SignalColumn;
    public const string CalculusHeader = DataHeader + "," + IntegralColumn + "," + DerivativeColumn;

    internal const string SourceSection = "source";
    internal const string MeasurementSection = "measurement";
    internal const string OriginalHeaderSection = "original header";
    internal const string ProcessingSection = "processing";
    internal const string ListSeparator = "; ";

    /// <summary>
    /// Writes to a temporary file next to <paramref name="path"/> and then moves it over <paramref name="path"/>,
    /// so an existing work file is only replaced once the new one is complete.
    /// </summary>
    public static void Write(string path, WorkFile file)
    {
        var temp = path + ".tmp";
        try
        {
            using (var writer = new StreamWriter(temp, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                Write(writer, file);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public static void Write(TextWriter writer, WorkFile file)
    {
        var n = file.FieldOe.Length;
        if (file.SignalMv.Length != n)
            throw new ArgumentException("Field and signal must have the same length.");
        var calculus = file.IntegralMvOe is not null || file.DerivativeMvPerOe is not null;
        if (calculus && (file.IntegralMvOe?.Length != n || file.DerivativeMvPerOe?.Length != n))
            throw new ArgumentException("Integral and derivative must both be given, with the length of the field.");

        writer.NewLine = "\n";
        writer.WriteLine($"{Signature}, format {FormatVersion}");

        Section(writer, SourceSection);
        Entry(writer, "file", file.Source.FilePath);
        Entry(writer, "imported", file.Source.Imported.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));

        var m = file.Metadata;
        Section(writer, MeasurementSection);
        Entry(writer, "sample", m.SampleName);
        Entry(writer, "recorded_signal", file.Recorded);
        Entry(writer, "frequency_GHz", Format(m.FrequencyGHz));
        Entry(writer, "power_dBm", Format(m.PowerDbm));
        Entry(writer, "lockin_phase_deg", Format(m.LockInPhaseDeg));
        Entry(writer, "sweep_rate_up_Oe_per_s", Format(file.SweepRateUpOePerS));
        Entry(writer, "sweep_rate_down_Oe_per_s", Format(file.SweepRateDownOePerS));
        Entry(writer, "comment", m.Comment is null ? null : Escape(m.Comment));
        Entry(writer, "lockin", m.LockInSettings.Count == 0 ? null : string.Join(ListSeparator, m.LockInSettings));

        Section(writer, OriginalHeaderSection);
        foreach (var line in m.PreambleLines)
            writer.WriteLine("# " + line.Replace("\r", "").Replace("\n", " "));

        var p = file.Processing;
        Section(writer, ProcessingSection);
        Entry(writer, "skipped_leading_rows", p.SkippedLeadingRows.ToString(CultureInfo.InvariantCulture));
        Entry(writer, "skipped_repeated_rows", p.SkippedRepeatedRows.ToString(CultureInfo.InvariantCulture));
        Entry(writer, "sweep_correction_Oe", Format(p.SweepCorrectionOe));
        Entry(writer, "phase_deg", Format(p.PhaseDeg));
        Entry(writer, "phase_change_deg", Format(p.PhaseChangeDeg));
        Entry(writer, "sweeps", p.Sweeps);
        Entry(writer, "time_background_V_Vps", List(p.TimeBackgroundIntercept, p.TimeBackgroundSlope));
        Entry(writer, "field_background_V_VpOe", List(p.FieldBackgroundIntercept, p.FieldBackgroundSlope));
        Entry(writer, "grid_from_to_step_Oe", List(p.GridFromOe, p.GridToOe, p.GridStepOe));
        Entry(writer, "smoothing", p.Smoothing);
        Entry(writer, "smoothing_points", p.SmoothingPoints?.ToString(CultureInfo.InvariantCulture));
        Entry(writer, "smoothing_polynomial_order", p.SmoothingPolynomialOrder?.ToString(CultureInfo.InvariantCulture));
        Entry(writer, "power_reference_dBm", Format(p.PowerReferenceDbm) ?? "off");
        Entry(writer, "power_factor", Format(p.PowerFactor) ?? "off");
        Entry(writer, "display", p.Display);
        Entry(writer, "display_offset", Format(p.DisplayOffset));
        Entry(writer, "display_offset_applied", p.DisplayOffsetApplied ? "yes" : "no");

        if (!calculus)
        {
            writer.WriteLine(DataHeader);
            for (var i = 0; i < n; i++)
                writer.WriteLine($"{Format(file.FieldOe[i])},{Format(file.SignalMv[i])}");
            return;
        }

        writer.WriteLine(CalculusHeader);
        for (var i = 0; i < n; i++)
            writer.WriteLine($"{Format(file.FieldOe[i])},{Format(file.SignalMv[i])}," +
                             $"{Format(file.IntegralMvOe![i])},{Format(file.DerivativeMvPerOe![i])}");
    }

    internal static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string? Format(double? value) => value is { } v ? Format(v) : null;

    private static string List(params double[] values) => string.Join(ListSeparator, values.Select(Format));

    internal static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n");

    private static void Section(TextWriter writer, string name) => writer.WriteLine($"# [{name}]");

    /// <summary>Writes "# key = value"; absent values are written as an empty value.</summary>
    private static void Entry(TextWriter writer, string key, string? value) =>
        writer.WriteLine($"# {key} = {value ?? ""}");
}
