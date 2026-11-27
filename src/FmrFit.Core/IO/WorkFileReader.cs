using System.Globalization;
using System.Text;

namespace FmrFit.Core.IO;

/// <summary>Reads files written by <see cref="WorkFileWriter"/>.</summary>
public static class WorkFileReader
{
    public static WorkFile Read(string path)
    {
        using var reader = new StreamReader(path);
        return Read(reader);
    }

    public static WorkFile Read(TextReader reader)
    {
        var first = reader.ReadLine();
        if (first is null || !first.StartsWith(WorkFileWriter.Signature, StringComparison.Ordinal))
            throw new FormatException("Not an FMR-fit work file (missing signature line).");

        var entries = new Dictionary<string, Dictionary<string, string>>();
        var preamble = new List<string>();
        string? section = null;
        var lineNumber = 1;
        string? line;

        // Header
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (!line.StartsWith('#'))
                break;

            var text = line.StartsWith("# ", StringComparison.Ordinal) ? line[2..] : line[1..];
            if (text.StartsWith('[') && text.EndsWith(']') && IsKnownSection(text[1..^1]))
            {
                section = text[1..^1];
                continue;
            }

            if (section == WorkFileWriter.OriginalHeaderSection)
            {
                preamble.Add(text);
                continue;
            }

            var eq = text.IndexOf(" = ", StringComparison.Ordinal);
            var (key, value) = eq >= 0 ? (text[..eq].Trim(), text[(eq + 3)..]) : (text.TrimEnd('=', ' '), "");
            if (section is not null && key.Length > 0)
            {
                if (!entries.TryGetValue(section, out var dict))
                    entries[section] = dict = [];
                dict[key] = value;
            }
        }

        var header = line?.Trim();
        if (header is not (WorkFileWriter.DataHeader or WorkFileWriter.CalculusHeader))
            throw new FormatException($"Line {lineNumber}: expected the data header '{WorkFileWriter.CalculusHeader}' " +
                                      $"or '{WorkFileWriter.DataHeader}'.");
        var calculus = header == WorkFileWriter.CalculusHeader;
        var columns = calculus ? 4 : 2;

        // Data
        var field = new List<double>();
        var signal = new List<double>();
        var integral = new List<double>();
        var derivative = new List<double>();
        var numbers = new double[columns];
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var cells = line.Split(',');
            if (cells.Length < columns || Enumerable.Range(0, columns).Any(c => !TryNumber(cells[c], out numbers[c])))
                throw new FormatException($"Line {lineNumber}: expected {columns} numbers, found '{line}'.");
            field.Add(numbers[0]);
            signal.Add(numbers[1]);
            if (calculus)
            {
                integral.Add(numbers[2]);
                derivative.Add(numbers[3]);
            }
        }

        string? Get(string sec, string key) =>
            entries.TryGetValue(sec, out var d) && d.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        double? Number(string sec, string key) => TryNumber(Get(sec, key), out var v) ? v : null;
        double[] Numbers(string sec, string key, int count)
        {
            var parts = (Get(sec, key) ?? "").Split(';', StringSplitOptions.TrimEntries);
            var result = new double[count];
            for (var i = 0; i < count; i++)
                result[i] = i < parts.Length && TryNumber(parts[i], out var v) ? v : 0;
            return result;
        }

        const string src = WorkFileWriter.SourceSection;
        const string meas = WorkFileWriter.MeasurementSection;
        const string proc = WorkFileWriter.ProcessingSection;

        var imported = DateTimeOffset.TryParse(Get(src, "imported"), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var t) ? t : DateTimeOffset.MinValue;
        var comment = Get(meas, "comment");
        var lockIn = Get(meas, "lockin");

        var metadata = new FmrMetadata(
            Get(meas, "sample"),
            Number(meas, "frequency_GHz"),
            Number(meas, "power_dBm"),
            comment is null ? null : Unescape(comment),
            lockIn is null ? [] : lockIn.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            Number(meas, "lockin_phase_deg"),
            preamble);

        var timeBg = Numbers(proc, "time_background_V_Vps", 2);
        var fieldBg = Numbers(proc, "field_background_V_VpOe", 2);
        var grid = Numbers(proc, "grid_from_to_step_Oe", 3);
        var processing = new WorkFileProcessing(
            (int)(Number(proc, "skipped_leading_rows") ?? 0),
            (int)(Number(proc, "skipped_repeated_rows") ?? 0),
            Number(proc, "sweep_correction_Oe") ?? 0,
            Number(proc, "phase_deg") ?? 0,
            Number(proc, "phase_change_deg") ?? 0,
            Get(proc, "sweeps") ?? "",
            timeBg[0], timeBg[1],
            fieldBg[0], fieldBg[1],
            grid[0], grid[1], grid[2],
            Number(proc, "power_reference_dBm"),
            Number(proc, "power_factor"),
            Get(proc, "smoothing") ?? "off",
            (int?)Number(proc, "smoothing_points"),
            (int?)Number(proc, "smoothing_polynomial_order"),
            Get(proc, "display") ?? "signal",
            Number(proc, "display_offset") ?? 0,
            Get(proc, "display_offset_applied")?.Trim().ToLowerInvariant() == "yes");

        return new WorkFile(
            new WorkFileSource(Get(src, "file"), imported),
            metadata,
            Number(meas, "sweep_rate_up_Oe_per_s"),
            Number(meas, "sweep_rate_down_Oe_per_s"),
            processing,
            [.. field],
            [.. signal],
            calculus ? [.. integral] : null,
            calculus ? [.. derivative] : null,
            Get(meas, "recorded_signal") ?? "dP/dH");
    }

    private static bool IsKnownSection(string name) =>
        name is WorkFileWriter.SourceSection or WorkFileWriter.MeasurementSection
            or WorkFileWriter.OriginalHeaderSection or WorkFileWriter.ProcessingSection;

    private static bool TryNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    internal static string Unescape(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                var next = text[++i];
                sb.Append(next switch { 'n' => '\n', _ => next });
            }
            else
            {
                sb.Append(text[i]);
            }
        }
        return sb.ToString();
    }
}
