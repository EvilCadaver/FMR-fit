using FmrFit.Core.IO;
using FmrFit.Core.Processing;

namespace FmrFit.Core.Fitting;

/// <summary>Converts between peaks with their fit result and the plain <see cref="FitFile"/> records written to disk.</summary>
public static class FitFileConversion
{
    /// <summary>
    /// The fit file for <paramref name="peaks"/> compared with the data in <paramref name="result"/>. Iterations are
    /// stored only if <paramref name="result"/> comes straight from a fit (not from evaluating changed peaks).
    /// <paramref name="range"/> is the fitted field range; null for the whole spectrum. <paramref name="data"/> is the
    /// work file column that was fitted; it sets the units of the amplitudes.
    /// </summary>
    public static FitFile ToFitFile(string workFileName, IReadOnlyList<Peak> peaks, FitResult result, DateTimeOffset saved,
        FieldRange? range = null, SignalView data = SignalView.Signal)
    {
        var dataFieldPower = SignalCalculus.FieldPower(data);
        var filePeaks = peaks.Select(peak =>
        {
            var shape = peak.Shape;
            var parameters = Enumerable.Range(0, peak.Parameters.Length).Select(k =>
            {
                var kind = shape.ParameterKinds[k];
                var value = kind == ParameterKind.Width ? Math.Abs(peak.Parameters[k]) : peak.Parameters[k];
                var error = peak.Errors is { } e && double.IsFinite(e[k]) ? e[k] : (double?)null;
                var range = peak.Range(k);
                return new FitFileParameter(shape.ParameterNames[k], value, error, ParameterUnits.AsciiOfData(kind, dataFieldPower),
                    range.Min, range.Max, peak.IsFixed(k));
            }).ToList();
            return new FitFilePeak(shape.Id, peak.KeepHeight, parameters);
        }).ToList();

        // Each shape's formula once, in order of first use; shapes not used are left out.
        var formulas = peaks.Select(p => p.Shape).DistinctBy(s => s.Id).Select(s => new FitFileFormula(s.Id, s.Formula)).ToList();

        var fitted = result.Iterations > 0;
        return new FitFile(workFileName, result.ChiSquared, result.ReducedChiSquared, result.RSquared,
            result.StandardDeviation, result.Points, result.FreeParameters,
            fitted ? result.Iterations : null, fitted ? result.ExitReason : null, saved, formulas, filePeaks,
            range?.Min, range?.Max, SignalViewText.ToText(data));
    }

    /// <summary>The work file column the fit in <paramref name="file"/> was made on.</summary>
    public static SignalView DataOf(FitFile file) => SignalViewText.Parse(file.Data);

    /// <summary>
    /// The fitted field range stored in <paramref name="file"/>, limited to <paramref name="data"/>; the whole data
    /// range if the file has none or its range does not overlap the data.
    /// </summary>
    public static FieldRange RangeOf(FitFile file, FieldRange data) =>
        file.RangeMinOe is { } min && file.RangeMaxOe is { } max
            ? FieldRange.Create(min, max, data) ?? data
            : data;

    /// <summary>
    /// The peaks stored in <paramref name="file"/>, with values, errors, limits, fixed parameters and height keeping.
    /// Widths and width ranges below <see cref="PeakLimits.MinWidth"/> are raised to it. Throws <see cref="FormatException"/> for an unknown shape, parameters that don't match the shape, or a
    /// minimum above its maximum.
    /// </summary>
    public static List<Peak> ToPeaks(FitFile file)
    {
        var peaks = new List<Peak>(file.Peaks.Count);
        for (var i = 0; i < file.Peaks.Count; i++)
        {
            var stored = file.Peaks[i];
            var shape = PeakShapes.ById(stored.Shape)
                ?? throw new FormatException($"Peak {i + 1}: unknown shape '{stored.Shape}'.");
            var names = shape.ParameterNames;
            if (stored.Parameters.Count != names.Count || !stored.Parameters.Select(p => p.Name).SequenceEqual(names))
                throw new FormatException(
                    $"Peak {i + 1}: expected parameters {string.Join(", ", names)} for {stored.Shape}, " +
                    $"found {string.Join(", ", stored.Parameters.Select(p => p.Name))}.");

            var parameters = stored.Parameters.Select(p => p.Value).ToArray();
            var errors = stored.Parameters.Any(p => p.Error is not null)
                ? stored.Parameters.Select(p => p.Error ?? double.NaN).ToArray()
                : null;
            var limits = stored.Parameters.Select(p => p.Min <= p.Max
                ? new ParameterRange(p.Min, p.Max)
                : throw new FormatException($"Peak {i + 1}, {p.Name}: minimum {p.Min} is above maximum {p.Max}.")).ToArray();
            var isFixed = stored.Parameters.Any(p => p.Fixed) ? stored.Parameters.Select(p => p.Fixed).ToArray() : null;
            peaks.Add(new Peak(shape, parameters, errors, limits, isFixed, stored.KeepHeight).WithMinimumWidths());
        }
        return peaks;
    }
}
