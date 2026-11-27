using System.Globalization;

namespace FmrFit.Core.IO;

/// <summary>Reads files written by <see cref="FitFileWriter"/>.</summary>
public static class FitFileReader
{
    public static FitFile Read(string path)
    {
        using var reader = new StreamReader(path);
        return Read(reader);
    }

    public static FitFile Read(TextReader reader)
    {
        var first = reader.ReadLine();
        if (first is null || !first.StartsWith(FitFileWriter.Signature, StringComparison.Ordinal))
            throw new FormatException("Not an FMR-fit fit file (missing signature line).");

        var fit = new Dictionary<string, string>();
        var formulas = new List<FitFileFormula>();
        var options = new Dictionary<int, (string Shape, bool KeepHeight)>();
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
            if (text.StartsWith('[') && text.EndsWith(']'))
            {
                section = text[1..^1];
                continue;
            }
            var eq = text.IndexOf(" = ", StringComparison.Ordinal);
            if (eq < 0)
                continue;
            var (key, value) = (text[..eq].Trim(), text[(eq + 3)..].Trim());

            if (section == FitFileWriter.FitSection)
                fit[key] = value;
            else if (section == FitFileWriter.FunctionsSection)
                formulas.Add(new FitFileFormula(key, value));
            else if (section == FitFileWriter.PeakOptionsSection && key.StartsWith(FitFileWriter.PeakKeyPrefix, StringComparison.Ordinal))
            {
                if (!int.TryParse(key[FitFileWriter.PeakKeyPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    throw new FormatException($"Line {lineNumber}: expected 'peak <number>', found '{key}'.");
                options[n] = ParseOptions(value, lineNumber);
            }
        }

        if (line is null || line.Trim() != FitFileWriter.TableHeader)
            throw new FormatException($"Line {lineNumber}: expected the table header '{FitFileWriter.TableHeader}'.");

        // Table: rows of each peak are consecutive, peaks numbered 1, 2, …
        var parameters = new SortedDictionary<int, List<FitFileParameter>>();
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var cells = line.Split(',');
            if (cells.Length != 8)
                throw new FormatException($"Line {lineNumber}: expected 8 cells, found {cells.Length}.");
            if (!int.TryParse(cells[0], NumberStyles.None, CultureInfo.InvariantCulture, out var peak) || peak < 1)
                throw new FormatException($"Line {lineNumber}: '{cells[0]}' is not a peak number.");
            var parameter = new FitFileParameter(
                cells[1].Trim(),
                Number(cells[2], "value"),
                cells[3].Trim().Length == 0 ? null : Number(cells[3], "error"),
                cells[4].Trim(),
                Number(cells[5], "min"),
                Number(cells[6], "max"),
                YesNo(cells[7], lineNumber));
            if (!parameters.TryGetValue(peak, out var list))
                parameters[peak] = list = [];
            list.Add(parameter);
        }

        var peaks = new List<FitFilePeak>();
        var count = Math.Max(parameters.Count == 0 ? 0 : parameters.Keys.Max(), options.Count == 0 ? 0 : options.Keys.Max());
        for (var n = 1; n <= count; n++)
        {
            if (!options.TryGetValue(n, out var o))
                throw new FormatException($"Peak {n} has no entry in [{FitFileWriter.PeakOptionsSection}].");
            if (!parameters.TryGetValue(n, out var list))
                throw new FormatException($"Peak {n} has no parameters in the table.");
            peaks.Add(new FitFilePeak(o.Shape, o.KeepHeight, list));
        }

        string? Get(string key) => fit.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        double Stat(string key) => TryNumber(Get(key), out var v) ? v : double.NaN;
        int? Integer(string key) =>
            int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

        double? rangeMin = null, rangeMax = null;
        if (Get("fit_range_Oe") is { } rangeText)
        {
            var ends = rangeText.Split(';', StringSplitOptions.TrimEntries);
            if (ends.Length != 2 || !TryNumber(ends[0], out var a) || !TryNumber(ends[1], out var b) || !(a < b))
                throw new FormatException($"fit_range_Oe '{rangeText}' is not 'min; max' with min < max.");
            (rangeMin, rangeMax) = (a, b);
        }

        var saved = DateTimeOffset.TryParse(Get("saved"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t : DateTimeOffset.MinValue;

        return new FitFile(
            Get("work_file") ?? "",
            Stat("chi_squared_mV2"),
            Stat("reduced_chi_squared_mV2"),
            Stat("r_squared"),
            Stat("standard_deviation_mV"),
            Integer("points") ?? 0,
            Integer("free_parameters") ?? 0,
            Integer("iterations"),
            Get("exit_reason"),
            saved,
            formulas,
            peaks,
            rangeMin,
            rangeMax,
            Get("data") ?? "signal");

        double Number(string text, string what) =>
            TryNumber(text.Trim(), out var v) ? v : throw new FormatException($"Line {lineNumber}: {what} '{text}' is not a number.");
    }

    /// <summary>Parses "LorentzianDerivative; keep_height = yes".</summary>
    private static (string Shape, bool KeepHeight) ParseOptions(string value, int lineNumber)
    {
        var parts = value.Split(';', StringSplitOptions.TrimEntries);
        var keepHeight = false;
        foreach (var part in parts.Skip(1))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim() == FitFileWriter.KeepHeightKey)
                keepHeight = YesNo(part[(eq + 1)..], lineNumber);
        }
        if (parts[0].Length == 0)
            throw new FormatException($"Line {lineNumber}: missing peak shape.");
        return (parts[0], keepHeight);
    }

    private static bool YesNo(string text, int lineNumber) => text.Trim() switch
    {
        "yes" => true,
        "no" => false,
        var other => throw new FormatException($"Line {lineNumber}: expected 'yes' or 'no', found '{other}'."),
    };

    private static bool TryNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
