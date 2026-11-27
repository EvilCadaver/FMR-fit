using System.Globalization;
using FmrFit.Core.IO;

namespace FmrFit.Core.Processing;

/// <summary>
/// Optional changes to each spectrum on export (the work files are not touched), done in this order:
/// <paramref name="Align"/> subtracts the straight line through the mean (field, value) of the first and of the last
/// <paramref name="AlignmentPoints"/> points, so both end averages become 0; <paramref name="Shift"/> subtracts the
/// mean of the last <paramref name="AlignmentPoints"/> points, so the high-field end averages 0;
/// <paramref name="Normalise"/> divides the spectrum by its largest |value|, giving values of unit 1.
/// </summary>
public sealed record ExportOperations(bool Align, int AlignmentPoints, bool Normalise, bool Shift = false)
{
    public const int DefaultAlignmentPoints = 10;

    public static ExportOperations None { get; } = new(false, DefaultAlignmentPoints, false);
}

/// <summary>
/// Combines work files into one frequency–field–value table for 3D plots (<see cref="MapExport"/>). The value is each
/// file's displayed column (<see cref="SignalCalculus.DisplayedView"/>). Together with what was recorded (dP/dH or ~P)
/// that column stands for a physical quantity (<see cref="MeasuredQuantity"/>), which must be the same for all files:
/// e.g. dP/dH integrated and ~P as recorded are both P.
/// </summary>
public static class MapExportBuilder
{
    /// <summary>Steps are compared after rounding to this many decimals, so float noise doesn't split equal steps.</summary>
    private const int StepDecimals = 6;

    /// <summary>
    /// One row per data point: spectra in order of frequency (files with the same frequency keep their given order),
    /// each spectrum's points in their stored field order. The field step is each file's interpolation grid step, or
    /// its most regular point spacing if the grid step is unknown.
    /// </summary>
    /// <exception cref="ArgumentException">No work files, one has no frequency, they stand for different quantities
    /// (see <see cref="Mismatch"/>), or <paramref name="operations"/> cannot be done on a spectrum (too few points to
    /// align or shift, or nothing but zeros to normalise).</exception>
    public static MapExport Build(IEnumerable<WorkFile> workFiles, ExportOperations? operations = null)
    {
        operations ??= ExportOperations.None;
        if ((operations.Align || operations.Shift) && operations.AlignmentPoints < 1)
            throw new ArgumentException("Aligning or shifting needs at least 1 point at each end.", nameof(operations));
        var files = workFiles.ToList();
        if (files.Count == 0)
            throw new ArgumentException("No work files to export.", nameof(workFiles));
        if (files.Any(f => f.Metadata.FrequencyGHz is null))
            throw new ArgumentException("Every work file needs a frequency.", nameof(workFiles));
        if (Mismatch(files) is { } mismatch)
            throw new ArgumentException(mismatch, nameof(workFiles));
        var order = MeasuredQuantity.Order(files[0]);

        // OrderBy is stable: equal frequencies keep the caller's order.
        files = files.OrderBy(f => f.Metadata.FrequencyGHz!.Value).ToList();
        var frequencies = files.Select(f => f.Metadata.FrequencyGHz!.Value).ToList();

        var columns = new List<double[]>(files.Count);
        var done = new List<MapExportOperation>();
        for (var k = 0; k < files.Count; k++)
        {
            var (values, operation) = Apply(files[k], operations);
            columns.Add(values);
            if (operation is not null)
                done.Add(operation);
        }
        var count = files.Select((f, k) => Math.Min(f.FieldOe.Length, columns[k].Length)).Sum();
        var fColumn = new double[count];
        var hColumn = new double[count];
        var vColumn = new double[count];
        var row = 0;
        for (var k = 0; k < files.Count; k++)
        {
            var file = files[k];
            var n = Math.Min(file.FieldOe.Length, columns[k].Length);
            for (var i = 0; i < n; i++, row++)
            {
                fColumn[row] = frequencies[k];
                hColumn[row] = file.FieldOe[i];
                vColumn[row] = columns[k][i];
            }
        }

        var samples = files
            .Select(f => f.Metadata.SampleName?.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .GroupBy(s => s!)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .ToList();

        var fieldSteps = files
            .Select(f => f.Processing.GridStepOe > 0 ? f.Processing.GridStepOe : MostRegularStep(f.FieldOe))
            .OfType<double>()
            .Select(s => Math.Round(s, StepDecimals))
            .Distinct()
            .Order()
            .ToList();

        return new MapExport(
            samples,
            frequencies[0],
            frequencies[^1],
            MostRegularStep(frequencies),
            fieldSteps,
            files.Count,
            fColumn,
            hColumn,
            vColumn,
            MeasuredQuantity.Name(order),
            operations.Normalise ? MeasuredQuantity.ColumnName(order) + "_1" : ValueColumn(files, order),
            Sources(files),
            operations.Align ? operations.AlignmentPoints : null,
            operations.Normalise,
            done,
            operations.Shift ? operations.AlignmentPoints : null);
    }

    /// <summary>
    /// The file's displayed column (<see cref="SignalCalculus.DisplayedColumn"/>) with <paramref name="operations"/>
    /// done, and what was done (null if nothing).
    /// </summary>
    private static (double[] Values, MapExportOperation? Operation) Apply(WorkFile file, ExportOperations operations)
    {
        var frequency = file.Metadata.FrequencyGHz!.Value;
        var values = SignalCalculus.DisplayedColumn(file);
        var n = Math.Min(file.FieldOe.Length, values.Length);
        var name = $"The {frequency.ToString("0.###", CultureInfo.InvariantCulture)} GHz spectrum";
        double? intercept = null, slope = null, divisor = null;

        if (operations.Align)
        {
            var points = operations.AlignmentPoints;
            if (n < 2 * points)
                throw new ArgumentException(
                    $"{name} has {n} points; aligning with {points} points at each end needs at least {2 * points}.");
            var (h1, v1) = Mean(file.FieldOe, values, 0, points);
            var (h2, v2) = Mean(file.FieldOe, values, n - points, points);
            var s = h2 != h1 ? (v2 - v1) / (h2 - h1) : 0;
            var c = v1 - s * h1;
            for (var i = 0; i < n; i++)
                values[i] -= c + s * file.FieldOe[i];
            (intercept, slope) = (c, s);
        }

        if (operations.Shift)
        {
            var points = operations.AlignmentPoints;
            if (n < points)
                throw new ArgumentException($"{name} has {n} points; shifting by the mean of the last {points} needs at least {points}.");
            var (_, shift) = Mean(file.FieldOe, values, n - points, points);
            for (var i = 0; i < n; i++)
                values[i] -= shift;
            // A constant is a baseline without slope; after aligning it adds to the line's intercept.
            intercept = (intercept ?? 0) + shift;
        }

        if (operations.Normalise)
        {
            var largest = 0.0;
            for (var i = 0; i < n; i++)
                if (double.IsFinite(values[i]))
                    largest = Math.Max(largest, Math.Abs(values[i]));
            if (largest == 0)
            {
                var after = (operations.Align, operations.Shift) switch
                {
                    (true, true) => " after aligning and shifting",
                    (true, false) => " after aligning",
                    (false, true) => " after shifting",
                    _ => "",
                };
                throw new ArgumentException($"{name} is zero everywhere{after}: it cannot be normalised.");
            }
            for (var i = 0; i < n; i++)
                values[i] /= largest;
            divisor = largest;
        }

        return (values, operations.Align || operations.Shift || operations.Normalise
            ? new MapExportOperation(frequency, intercept, slope, divisor)
            : null);
    }

    private static (double Field, double Value) Mean(double[] field, double[] values, int start, int count)
    {
        double h = 0, v = 0;
        for (var i = start; i < start + count; i++)
        {
            h += field[i];
            v += values[i];
        }
        return (h / count, v / count);
    }

    /// <summary>
    /// Quantity name with its unit when all files share it (e.g. "P_mV"); the name alone (e.g. "P") when the units
    /// differ, such as ~P as recorded (mV) and dP/dH integrated (mV·Oe).
    /// </summary>
    private static string ValueColumn(IReadOnlyList<WorkFile> files, int order)
    {
        var powers = files.Select(f => SignalCalculus.FieldPower(SignalCalculus.DisplayedView(f))).Distinct().ToList();
        var name = MeasuredQuantity.ColumnName(order);
        return powers.Count == 1 ? name + "_" + MeasuredQuantity.UnitSuffix(powers[0]) : name;
    }

    /// <summary>"dP/dH integral (2); ~P signal (3)": what was recorded and which column, with the number of files.</summary>
    private static string Sources(IEnumerable<WorkFile> files) => string.Join("; ", files
        .GroupBy(f => (Recorded: RecordedSignalText.Parse(f.Recorded), View: SignalCalculus.DisplayedView(f)))
        .OrderBy(g => g.Key.Recorded).ThenBy(g => g.Key.View)
        .Select(g => $"{RecordedSignalText.ToText(g.Key.Recorded)} {SignalViewText.ToText(g.Key.View)} ({g.Count()})"));

    /// <summary>
    /// Why the files cannot be exported together: their displayed columns stand for different quantities
    /// (e.g. "P (3), dP/dH (1)"); null if they all give the same quantity.
    /// </summary>
    public static string? Mismatch(IEnumerable<WorkFile> workFiles)
    {
        var groups = workFiles.GroupBy(MeasuredQuantity.Order).OrderBy(g => g.Key).ToList();
        return groups.Count <= 1
            ? null
            : "The work files show different quantities: "
              + string.Join(", ", groups.Select(g => $"{MeasuredQuantity.Name(g.Key)} ({g.Count()})"))
              + ". Change the display with Modify… so that all give the same one (dP/dH as recorded, or its integral"
              + " and ~P as recorded, both give P).";
    }

    /// <summary>
    /// The most common spacing between neighbouring distinct values (sorted first), rounded to 6 decimals; the smaller
    /// one on a tie. Null if there are fewer than two distinct values. E.g. 30, 31, 32, 34, 35 → 1.
    /// </summary>
    public static double? MostRegularStep(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).Order().ToList();
        var steps = new List<double>();
        for (var i = 1; i < sorted.Count; i++)
        {
            var step = Math.Round(sorted[i] - sorted[i - 1], StepDecimals);
            if (step > 0) // repeated values are not a step
                steps.Add(step);
        }

        return steps.Count == 0
            ? null
            : steps.GroupBy(s => s)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .First().Key;
    }
}
