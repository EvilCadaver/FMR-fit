using System.Globalization;
using System.Text;

namespace FmrFit.Core.IO;

/// <summary>
/// Writes fit files ("[work file name].fit.csv"): a "#"-commented header, then one table row per peak parameter.
/// <code>
/// # FMR-fit peak fit, format 2
/// # [fit]
/// # work_file = MM100610_cprllH_30GHz.csv
/// # data = signal                           &lt;- fitted column: signal (mV), integral (mV*Oe) or derivative (mV/Oe)
/// # chi_squared_mV2 = 4.7012E-06            &lt;- Σ (data − fit)², in the fitted column's unit squared
/// # reduced_chi_squared_mV2 = 3.8689E-09    &lt;- χ² / (points − free_parameters)
/// # r_squared = 0.99731                     &lt;- 1 − χ² / Σ (data − mean)²
/// # standard_deviation_mV = 6.22E-05        &lt;- √(reduced χ²)
/// # points = 1223
/// # free_parameters = 8
/// # fit_range_Oe = 4500; 8000              &lt;- points used for the fit and the statistics; empty = whole spectrum
/// # iterations = 26                         &lt;- empty if the peaks changed after the last fit
/// # exit_reason = RelativePoints
/// # saved = 2026-11-24T15:03:00+01:00
/// # [functions]                             &lt;- only the shapes used
/// # LorentzianDerivative = f(H) = -A1*4*w*u/(4*u^2 + w^2)^2 - A2*(w^2 - 4*u^2)/(4*u^2 + w^2)^2, with u = H - xc
/// # [peak options]
/// # peak 1 = LorentzianDerivative; keep_height = yes
/// peak,parameter,value,error,unit,min,max,fixed
/// 1,xc,781.88,1.1439,Oe,120,12270,no
/// 1,A2,-3597.3,,mV*Oe^2,-97341,97341,yes    &lt;- empty error: fixed or not fitted
/// </code>
/// Numbers use invariant culture and round-trip formatting, so a file reloads exactly; NaN is written as empty.
/// Plain CSV: every "#" line is a comment, then the header row and one row per parameter.
/// </summary>
public static class FitFileWriter
{
    public const string Signature = "# FMR-fit peak fit";
    public const int FormatVersion = 2;
    public const string TableHeader = "peak,parameter,value,error,unit,min,max,fixed";

    internal const string FitSection = "fit";
    internal const string FunctionsSection = "functions";
    internal const string PeakOptionsSection = "peak options";
    internal const string PeakKeyPrefix = "peak ";
    internal const string KeepHeightKey = "keep_height";
    internal const string ListSeparator = "; ";

    /// <summary>Writes to a temporary file next to <paramref name="path"/>, then moves it over <paramref name="path"/>.</summary>
    public static void Write(string path, FitFile file)
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

    public static void Write(TextWriter writer, FitFile file)
    {
        writer.NewLine = "\n";
        writer.WriteLine($"{Signature}, format {FormatVersion}");

        writer.WriteLine($"# [{FitSection}]");
        Entry("work_file", file.WorkFile);
        Entry("data", file.Data);
        Entry("chi_squared_mV2", Format(file.ChiSquaredMv2));
        Entry("reduced_chi_squared_mV2", Format(file.ReducedChiSquaredMv2));
        Entry("r_squared", Format(file.RSquared));
        Entry("standard_deviation_mV", Format(file.StandardDeviationMv));
        Entry("points", file.Points.ToString(CultureInfo.InvariantCulture));
        Entry("free_parameters", file.FreeParameters.ToString(CultureInfo.InvariantCulture));
        Entry("fit_range_Oe", file.RangeMinOe is { } min && file.RangeMaxOe is { } max
            ? Format(min) + ListSeparator + Format(max)
            : "");
        Entry("iterations", file.Iterations?.ToString(CultureInfo.InvariantCulture) ?? "");
        Entry("exit_reason", file.ExitReason ?? "");
        Entry("saved", file.Saved.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));

        if (file.Formulas.Count > 0)
        {
            writer.WriteLine($"# [{FunctionsSection}]");
            foreach (var f in file.Formulas)
                Entry(f.Shape, OneLine(f.Formula));
        }

        writer.WriteLine($"# [{PeakOptionsSection}]");
        for (var i = 0; i < file.Peaks.Count; i++)
        {
            var peak = file.Peaks[i];
            Entry($"{PeakKeyPrefix}{i + 1}", $"{peak.Shape}; {KeepHeightKey} = {YesNo(peak.KeepHeight)}");
        }

        writer.WriteLine(TableHeader);
        for (var i = 0; i < file.Peaks.Count; i++)
            foreach (var p in file.Peaks[i].Parameters)
                writer.WriteLine(string.Join(',',
                    (i + 1).ToString(CultureInfo.InvariantCulture), Cell(p.Name), Format(p.Value),
                    p.Error is { } e ? Format(e) : "", Cell(p.Unit), Format(p.Min), Format(p.Max), YesNo(p.Fixed)));

        void Entry(string key, string value) => writer.WriteLine($"# {key} = {value}");
    }

    internal static string YesNo(bool value) => value ? "yes" : "no";

    private static string OneLine(string text) => text.Replace("\r", " ").Replace("\n", " ");

    /// <summary>Round-trip number; NaN as empty.</summary>
    internal static string Format(double value) =>
        double.IsNaN(value) ? "" : value.ToString("R", CultureInfo.InvariantCulture);

    private static string Cell(string text)
    {
        if (text.Contains(',') || text.Contains('\n') || text.Contains('\r'))
            throw new ArgumentException($"'{text}' cannot be written to a fit file cell.");
        return text;
    }
}
