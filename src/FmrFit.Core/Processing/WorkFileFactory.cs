using FmrFit.Core.IO;

namespace FmrFit.Core.Processing;

/// <summary>Builds the <see cref="WorkFile"/> for a processed measurement.</summary>
public static class WorkFileFactory
{
    /// <summary>
    /// Runs <see cref="XSignalProcessing.Process"/> with the given corrections and settings and packs the result
    /// (signal in mV, with its integral and derivative over field) together with the measurement's metadata and the
    /// processing used, including which of the three columns to display.
    /// </summary>
    /// <param name="sourcePath">Full path of the measurement file, stored in the work file.</param>
    /// <param name="phaseDeg">Absolute lock-in phase used; the rotation is relative to the file's recorded phase.</param>
    /// <param name="normalizePower">Normalize to <see cref="PowerNormalization.ReferenceDbm"/> if the power is known;
    /// overrides <see cref="XProcessingSettings.Scale"/>.</param>
    /// <param name="recorded">What the measurement recorded (chosen by the user), stored in the work file.</param>
    public static WorkFile Create(
        string? sourcePath,
        FmrMeasurement measurement,
        FieldSweeps sweeps,
        double fieldCorrection,
        double phaseDeg,
        XProcessingSettings settings,
        bool normalizePower,
        DateTimeOffset imported,
        RecordedSignal recorded = RecordedSignal.Derivative)
    {
        var filePhase = measurement.Metadata.LockInPhaseDeg ?? 0;
        var phaseChange = phaseDeg - filePhase;
        var power = normalizePower ? measurement.Metadata.PowerDbm : null;
        double? factor = power is { } p ? PowerNormalization.Factor(p) : null;
        settings = settings with { Scale = factor ?? 1 };

        var (field, x) = XSignalProcessing.Process(sweeps, fieldCorrection, phaseChange, settings);
        var millivolts = x.Select(v => v * 1000).ToArray();
        var smoothing = (settings.Smoothing ?? SmoothingSettings.None).Coerce();

        var processing = new WorkFileProcessing(
            FmrCsvReader.DefaultSkippedLeadingRows,
            measurement.SkippedRepeatedRows,
            fieldCorrection,
            phaseDeg,
            phaseChange,
            SweepSelectionText.ToText(settings.Sweeps),
            settings.TimeBackground.Intercept,
            settings.TimeBackground.Slope,
            settings.FieldBackground.Intercept,
            settings.FieldBackground.Slope,
            settings.Grid.From,
            settings.Grid.To,
            settings.Grid.Step,
            factor is null ? null : PowerNormalization.ReferenceDbm,
            factor,
            SmoothingMethodText.ToText(smoothing.Method),
            smoothing.Method == SmoothingMethod.None ? null : smoothing.Points,
            smoothing.Method == SmoothingMethod.SavitzkyGolay ? smoothing.PolynomialOrder : null,
            SignalViewText.ToText(settings.Display),
            // The offset is given in V-based units and stored in the column's mV-based unit; the data stay unshifted.
            settings.Offset * 1000,
            settings.ApplyOffset);

        return new WorkFile(
            new WorkFileSource(sourcePath, imported),
            measurement.Metadata,
            sweeps.UpRate,
            sweeps.DownRate,
            processing,
            field,
            millivolts,
            SignalCalculus.Integrate(field, millivolts),
            SignalCalculus.Differentiate(field, millivolts),
            RecordedSignalText.ToText(recorded));
    }
}
