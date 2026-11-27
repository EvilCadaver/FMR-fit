using System.Globalization;

namespace FmrFit.Core.IO;

/// <summary>
/// Reads FMR measurement CSV files of the form:
/// <code>
/// Sample Name, MM100610_cprllH                     &lt;- preamble, any number of lines
/// Freq, 29, GHz, Power, 15, dBm, &lt;free comment, may contain line breaks&gt;
/// SR850: f = 25 Hz, phase = 96.5 deg, ..., tc = 300 ms, 12dB/oct
/// Time,Field,X,Y,R,theta      &lt;- column header, located by name
/// s,Oe,V,V,V,deg              &lt;- optional units row
/// 1.59E-5,1.523E+2,...        &lt;- data
/// </code>
/// Only Time, Field, X and Y are read; other columns (R, theta) are ignored.
/// The first <see cref="DefaultSkippedLeadingRows"/> data rows are discarded: they can hold
/// leftovers from the previous run (stale time or field) and the field settling at the start.
/// After that, a data row whose Time equals the previous kept row's Time is a stale repeat of
/// the logger and is dropped.
/// </summary>
public static class FmrCsvReader
{
    public const int DefaultSkippedLeadingRows = 5;

    private static readonly string[] RequiredColumns = ["Time", "Field", "X", "Y"];

    /// <summary>The free comment starts after this marker (end of the "Power, 15, dBm," entry).</summary>
    private const string CommentStartMarker = "dBm,";

    /// <summary>Lock-in settings line; also ends the free comment.</summary>
    private const string LockInMarker = "SR850:";

    public static FmrMeasurement Read(string path, int skipLeadingRows = DefaultSkippedLeadingRows)
    {
        using var reader = new StreamReader(path);
        return Read(reader, skipLeadingRows);
    }

    public static FmrMeasurement Read(TextReader reader, int skipLeadingRows = DefaultSkippedLeadingRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skipLeadingRows);

        var preamble = new List<string>();
        int[]? columnIndex = null;
        var headerWidth = 0;
        var lineNumber = 0;
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            var cells = SplitCells(line);
            columnIndex = TryMapHeader(cells);
            if (columnIndex is not null)
            {
                headerWidth = cells.Length;
                break;
            }
            preamble.Add(line);
        }

        if (columnIndex is null)
            throw new FormatException("No column header containing Time, Field, X and Y was found.");

        var time = new List<double>();
        var field = new List<double>();
        var x = new List<double>();
        var y = new List<double>();
        var skipped = 0;
        var firstRowAfterHeader = true;

        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var cells = SplitCells(line);

            // Optional units row (e.g. "s,Oe,V,V,V,deg") directly below the header.
            if (firstRowAfterHeader)
            {
                firstRowAfterHeader = false;
                if (!TryParseNumber(cells[columnIndex[0]], out _))
                    continue;
            }

            if (skipLeadingRows > 0)
            {
                skipLeadingRows--;
                continue;
            }

            if (cells.Length < headerWidth)
                throw new FormatException(
                    $"Line {lineNumber}: expected {headerWidth} columns, found {cells.Length}.");

            var t = ParseNumber(cells, columnIndex[0], lineNumber);
            if (time.Count > 0 && t == time[^1])
            {
                skipped++;
                continue;
            }

            time.Add(t);
            field.Add(ParseNumber(cells, columnIndex[1], lineNumber));
            x.Add(ParseNumber(cells, columnIndex[2], lineNumber));
            y.Add(ParseNumber(cells, columnIndex[3], lineNumber));
        }

        return new FmrMeasurement(
            ParseMetadata(preamble),
            [.. time], [.. field], [.. x], [.. y],
            skipped);
    }

    private static string[] SplitCells(string line) =>
        line.Split(',', StringSplitOptions.TrimEntries);

    /// <summary>Returns the indices of Time, Field, X, Y if <paramref name="cells"/> is the header row.</summary>
    private static int[]? TryMapHeader(string[] cells)
    {
        var indices = new int[RequiredColumns.Length];
        for (var i = 0; i < RequiredColumns.Length; i++)
        {
            indices[i] = Array.FindIndex(cells,
                c => string.Equals(c, RequiredColumns[i], StringComparison.OrdinalIgnoreCase));
            if (indices[i] < 0)
                return null;
        }
        return indices;
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static double ParseNumber(string[] cells, int index, int lineNumber)
    {
        if (!TryParseNumber(cells[index], out var value))
            throw new FormatException(
                $"Line {lineNumber}, column {index + 1}: '{cells[index]}' is not a number.");
        return value;
    }

    /// <summary>
    /// Splits the preamble into: structured part (sample name, Freq, Power) up to "dBm,";
    /// free comment from there up to "SR850:"; and the lock-in settings on the rest of the SR850 line.
    /// Working on the joined text keeps a comment that contains line breaks intact.
    /// </summary>
    private static FmrMetadata ParseMetadata(List<string> preamble)
    {
        var text = string.Join('\n', preamble);

        var commentStart = text.IndexOf(CommentStartMarker, StringComparison.Ordinal);
        if (commentStart >= 0)
            commentStart += CommentStartMarker.Length;
        var lockInStart = text.IndexOf(LockInMarker, Math.Max(commentStart, 0), StringComparison.Ordinal);

        var structuredEnd = commentStart >= 0 ? commentStart : lockInStart >= 0 ? lockInStart : text.Length;
        string? sampleName = null;
        double? frequency = null;
        double? power = null;
        foreach (var line in text[..structuredEnd].Split('\n'))
        {
            var cells = SplitCells(line);
            if (cells.Length >= 2 && string.Equals(cells[0], "Sample Name", StringComparison.OrdinalIgnoreCase))
                sampleName ??= cells[1];
            frequency ??= NumberAfterKey(cells, "Freq");
            power ??= NumberAfterKey(cells, "Power");
        }

        string? comment = null;
        if (commentStart >= 0)
        {
            var commentEnd = lockInStart >= 0 ? lockInStart : text.Length;
            comment = text[commentStart..commentEnd].Trim(' ', '\t', '\n', ',');
            if (comment.Length == 0)
                comment = null;
        }

        IReadOnlyList<string> lockIn = [];
        if (lockInStart >= 0)
        {
            var start = lockInStart + LockInMarker.Length;
            var end = text.IndexOf('\n', start);
            lockIn = text[start..(end >= 0 ? end : text.Length)]
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        return new FmrMetadata(sampleName, frequency, power, comment, lockIn,
            LockInNumber(lockIn, "phase"), preamble);
    }

    /// <summary>Number at the start of the value of a "key = value unit" lock-in setting, e.g. 96.5 from "phase = 96.5 deg".</summary>
    private static double? LockInNumber(IReadOnlyList<string> settings, string key)
    {
        foreach (var item in settings)
        {
            var parts = item.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !string.Equals(parts[0], key, StringComparison.OrdinalIgnoreCase))
                continue;
            var number = parts[1].Split(' ', 2)[0];
            return TryParseNumber(number, out var value) ? value : null;
        }
        return null;
    }

    private static double? NumberAfterKey(string[] cells, string key)
    {
        for (var i = 0; i < cells.Length - 1; i++)
        {
            if (string.Equals(cells[i], key, StringComparison.OrdinalIgnoreCase)
                && TryParseNumber(cells[i + 1], out var value))
                return value;
        }
        return null;
    }
}
