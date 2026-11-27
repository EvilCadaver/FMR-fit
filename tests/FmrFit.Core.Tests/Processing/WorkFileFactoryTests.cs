using FmrFit.Core.IO;
using FmrFit.Core.Processing;

namespace FmrFit.Core.Tests.Processing;

public class WorkFileFactoryTests
{
    private static readonly FmrMeasurement Measurement =
        FmrCsvReader.Read(Path.Combine(AppContext.BaseDirectory, "data", "MM100610_cprllH_42G.csv"));

    private static readonly FieldSweeps Sweeps = FieldSweeps.FromMeasurement(Measurement);

    private static XProcessingSettings Settings() => new(
        SweepSelection.UpAndDown,
        new LinearBackground(1e-7, 2e-9),
        new LinearBackground(3e-6, -5e-10),
        XSignalProcessing.GuessGrid(Sweeps, 21, SweepSelection.UpAndDown));

    [Fact]
    public void Signal_is_the_processed_X_in_millivolts_with_power_normalization()
    {
        var file = WorkFileFactory.Create("42G.csv", Measurement, Sweeps, 21, 97.4, Settings(), normalizePower: true, DateTimeOffset.Now);

        var factor = PowerNormalization.Factor(15);
        var (field, x) = XSignalProcessing.Process(Sweeps, 21, 97.4 - 96.5, Settings() with { Scale = factor });
        Assert.Equal(field, file.FieldOe);
        for (var i = 0; i < x.Length; i++)
            Assert.Equal(x[i] * 1000, file.SignalMv[i], 12);

        Assert.Equal(10, file.Processing.PowerReferenceDbm);
        Assert.Equal(factor, file.Processing.PowerFactor);
    }

    [Fact]
    public void Records_the_processing_used()
    {
        var file = WorkFileFactory.Create("42G.csv", Measurement, Sweeps, 21, 97.4, Settings(), normalizePower: false, DateTimeOffset.Now);

        var p = file.Processing;
        Assert.Equal(5, p.SkippedLeadingRows);
        Assert.Equal(Measurement.SkippedRepeatedRows, p.SkippedRepeatedRows);
        Assert.Equal(21, p.SweepCorrectionOe);
        Assert.Equal(97.4, p.PhaseDeg);
        Assert.Equal(0.9, p.PhaseChangeDeg, 9);
        Assert.Equal("up&down", p.Sweeps);
        Assert.Equal((1e-7, 2e-9), (p.TimeBackgroundIntercept, p.TimeBackgroundSlope));
        Assert.Equal((3e-6, -5e-10), (p.FieldBackgroundIntercept, p.FieldBackgroundSlope));
        Assert.Null(p.PowerReferenceDbm);
        Assert.Null(p.PowerFactor);
        Assert.Equal(("off", (int?)null, (int?)null), (p.Smoothing, p.SmoothingPoints, p.SmoothingPolynomialOrder));
        Assert.Equal(Measurement.Metadata, file.Metadata);
        Assert.Equal(Sweeps.UpRate, file.SweepRateUpOePerS);
    }

    [Fact]
    public void Integral_and_derivative_of_the_signal_are_stored_with_the_display_choice()
    {
        var settings = Settings() with
        {
            Smoothing = SmoothingSettings.Default(SmoothingMethod.SavitzkyGolay),
            Display = SignalView.Integral,
        };

        var file = WorkFileFactory.Create("42G.csv", Measurement, Sweeps, 21, 97.4, settings, normalizePower: true, DateTimeOffset.Now);

        // Calculated from the stored (smoothed, normalized) signal in mV.
        Assert.Equal(SignalCalculus.Integrate(file.FieldOe, file.SignalMv), file.IntegralMvOe);
        Assert.Equal(SignalCalculus.Differentiate(file.FieldOe, file.SignalMv), file.DerivativeMvPerOe);
        Assert.Equal("integral", file.Processing.Display);
        Assert.Equal("dP/dH", file.Recorded);
        Assert.Equal("~P", WorkFileFactory.Create("42G.csv", Measurement, Sweeps, 21, 97.4, Settings(),
            normalizePower: false, DateTimeOffset.Now, RecordedSignal.Absorption).Recorded);
        Assert.Equal("signal", WorkFileFactory.Create("42G.csv", Measurement, Sweeps, 21, 97.4, Settings(),
            normalizePower: false, DateTimeOffset.Now).Processing.Display);
    }

    [Fact]
    public void Offset_is_recorded_but_never_written_into_the_data()
    {
        var plain = WorkFileFactory.Create("42G.csv", Measurement, Sweeps, 21, 97.4,
            Settings() with { Display = SignalView.Integral }, normalizePower: false, DateTimeOffset.Now);
        var withOffset = WorkFileFactory.Create("42G.csv", Measurement, Sweeps, 21, 97.4,
            Settings() with { Display = SignalView.Integral, ApplyOffset = true, Offset = 2e-3 }, normalizePower: false,
            DateTimeOffset.Now);

        // Given in V-based units (V·Oe here), stored in the column's mV-based unit.
        Assert.Equal((2.0, true), (withOffset.Processing.DisplayOffset, withOffset.Processing.DisplayOffsetApplied));
        Assert.Equal((0.0, false), (plain.Processing.DisplayOffset, plain.Processing.DisplayOffsetApplied));
        Assert.Equal(plain.SignalMv, withOffset.SignalMv);
        Assert.Equal(plain.IntegralMvOe, withOffset.IntegralMvOe);
        Assert.Equal(plain.DerivativeMvPerOe, withOffset.DerivativeMvPerOe);
        // Shown, fitted and exported with the offset.
        Assert.Equal(plain.IntegralMvOe![10] + 2, SignalCalculus.DisplayedColumn(withOffset)[10], 9);
    }

    [Fact]
    public void A_switched_off_offset_is_kept_but_not_used()
    {
        var file = WorkFileFactory.Create("42G.csv", Measurement, Sweeps, 21, 97.4,
            Settings() with { ApplyOffset = false, Offset = 5e-6 }, normalizePower: false, DateTimeOffset.Now);

        Assert.Equal((5e-3, false), (file.Processing.DisplayOffset, file.Processing.DisplayOffsetApplied));
        Assert.Equal(file.SignalMv, SignalCalculus.DisplayedColumn(file));
    }

    [Fact]
    public void Records_the_smoothing_used_and_smooths_the_signal()
    {
        var smoothing = new SmoothingSettings(SmoothingMethod.SavitzkyGolay, 7, 3);
        var settings = Settings() with { Smoothing = smoothing };

        var file = WorkFileFactory.Create("42G.csv", Measurement, Sweeps, 21, 97.4, settings, normalizePower: false, DateTimeOffset.Now);

        var p = file.Processing;
        Assert.Equal(("savitzky-golay", (int?)7, (int?)3), (p.Smoothing, p.SmoothingPoints, p.SmoothingPolynomialOrder));
        var (_, x) = XSignalProcessing.Process(Sweeps, 21, 97.4 - 96.5, settings);
        Assert.Equal(x[100] * 1000, file.SignalMv[100], 12);

        var fft = WorkFileFactory.Create("42G.csv", Measurement, Sweeps, 21, 97.4,
            Settings() with { Smoothing = SmoothingSettings.Default(SmoothingMethod.Fft) }, normalizePower: false, DateTimeOffset.Now);
        Assert.Equal(("fft", (int?)3, (int?)null),
            (fft.Processing.Smoothing, fft.Processing.SmoothingPoints, fft.Processing.SmoothingPolynomialOrder));
    }
}
