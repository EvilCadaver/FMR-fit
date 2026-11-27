using FmrFit.Core.IO;
using FmrFit.Core.Processing;

namespace FmrFit.Core.Tests.Processing;

public class SweepLagCorrectionTests
{
    private const double UpRate = 50;     // Oe/s
    private const double DownRate = -40;  // Oe/s
    private const double StartField = 100;
    private const double TopField = 3100;

    /// <summary>Field swept up then down; the recorded signal lags the field by <paramref name="delay"/>.</summary>
    private static FmrMeasurement Synthetic(double delay, bool withPeak, double noise = 0)
    {
        const double dt = 0.2;
        var upTime = (TopField - StartField) / UpRate;
        var totalTime = upTime + (TopField - StartField) / -DownRate;
        double FieldAt(double t) => t <= upTime ? StartField + UpRate * t : TopField + DownRate * (t - upTime);

        var random = new Random(1);
        var time = new List<double>();
        var field = new List<double>();
        var x = new List<double>();
        var y = new List<double>();
        for (var t = 0.0; t <= totalTime; t += dt)
        {
            var lagged = FieldAt(Math.Max(0, t - delay));
            var u = (lagged - 1600) / 30;
            var absorption = withPeak ? 1 / (1 + u * u) : 0;
            var dispersion = absorption * u;
            time.Add(t);
            field.Add(FieldAt(t));
            x.Add(absorption + noise * (random.NextDouble() - 0.5));
            y.Add(dispersion + noise * (random.NextDouble() - 0.5));
        }

        return new FmrMeasurement(FmrMetadata.Empty,
            [.. time], [.. field], [.. x], [.. y], 0);
    }

    [Fact]
    public void Splits_at_highest_field_and_fits_rates()
    {
        var s = FieldSweeps.FromMeasurement(Synthetic(0, withPeak: true));

        Assert.Equal(TopField, s.Up.Field[^1], 6);
        Assert.True(s.Down.Field[0] < TopField);
        Assert.Equal(UpRate, s.UpRate, 6);
        Assert.Equal(DownRate, s.DownRate, 6);
    }

    [Fact]
    public void Rejects_data_without_a_down_sweep()
    {
        var m = Synthetic(0, withPeak: true);
        var upOnly = new FmrMeasurement(m.Metadata, m.Time[..100], m.Field[..100], m.X[..100], m.Y[..100], 0);
        Assert.Throws<InvalidOperationException>(() => FieldSweeps.FromMeasurement(upOnly));
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.45)]
    [InlineData(1.3)]
    public void Recovers_known_delay(double delay)
    {
        var s = FieldSweeps.FromMeasurement(Synthetic(delay, withPeak: true));

        var estimate = SweepLagCorrection.Estimate(s);

        Assert.Equal(delay, estimate.Delay, 0.01);
        Assert.False(estimate.AtSearchLimit);
        Assert.True(estimate.Reduction > 0.9, $"Reduction {estimate.Reduction:P0}");
    }

    [Fact]
    public void Zero_delay_is_at_the_end_of_the_default_window()
    {
        // The default window starts at 0 (a negative delay is not physical), so no lag at all cannot be told from a
        // best match below the window.
        var estimate = SweepLagCorrection.Estimate(FieldSweeps.FromMeasurement(Synthetic(0, withPeak: true)));

        Assert.Equal(0, estimate.Delay, 0.001);
        Assert.True(estimate.AtSearchLimit);
    }

    [Fact]
    public void Field_correction_is_subtracted_on_up_sweep_and_added_on_down_sweep()
    {
        var s = FieldSweeps.FromMeasurement(Synthetic(0.5, withPeak: true));

        var (up, down) = SweepLagCorrection.ApplyFieldCorrection(s, 22.5);

        Assert.Equal(s.Up.Field[10] - 22.5, up[10], 6);
        Assert.Equal(s.Down.Field[10] + 22.5, down[10], 6);
    }

    [Fact]
    public void Field_correction_uses_mean_sweep_speed()
    {
        var s = FieldSweeps.FromMeasurement(Synthetic(0, withPeak: true));

        // up 50 Oe/s, down -40 Oe/s -> mean speed 45 Oe/s
        Assert.Equal(22.5, SweepLagCorrection.FieldCorrectionFor(s, 0.5), 6);
    }

    [Fact]
    public void Without_a_peak_the_correction_gives_little_improvement()
    {
        var s = FieldSweeps.FromMeasurement(Synthetic(0.5, withPeak: false, noise: 0.01));

        var estimate = SweepLagCorrection.Estimate(s);

        Assert.True(estimate.Reduction < 0.3, $"Reduction {estimate.Reduction:P0}");
    }

    [Theory]
    [InlineData(-2.0, -1.0, 5.0, -1.0)] // best delay below the window: its lower end
    [InlineData(2.0, -1.0, 1.0, 1.0)]   // above: its upper end
    public void A_delay_outside_the_search_window_gives_the_nearest_end(double delay, double min, double max, double expected)
    {
        var s = FieldSweeps.FromMeasurement(Synthetic(delay, withPeak: true));

        var estimate = SweepLagCorrection.Estimate(s, min, max);

        Assert.Equal(expected, estimate.Delay, 0.001);
        Assert.True(estimate.AtSearchLimit);
    }

    [Fact]
    public void Sample_file_with_a_negative_delay_is_estimated_within_the_window()
    {
        // Its up and down sweeps match best at about −2 s, which is not physical. Within the default window (0…5 s)
        // the mismatch falls towards 5 s, so the estimate ends at that end and is flagged as failed.
        var path = Path.Combine(AppContext.BaseDirectory, "data", "MERV_5.1_NG_42G.csv");
        var s = FieldSweeps.FromMeasurement(FmrCsvReader.Read(path));

        var narrow = SweepLagCorrection.Estimate(s);
        var wide = SweepLagCorrection.Estimate(s, -5, 5);

        Assert.Equal((5, true), (Math.Round(narrow.Delay, 3), narrow.AtSearchLimit));
        Assert.Equal(-2, wide.Delay, 0.1);
        Assert.False(wide.AtSearchLimit);
    }

    [Fact]
    public void Sample_file_gives_plausible_delay()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "MM100610_cprllH_42G.csv");
        var s = FieldSweeps.FromMeasurement(FmrCsvReader.Read(path));

        var estimate = SweepLagCorrection.Estimate(s);

        Assert.Equal(50, s.UpRate, 0.5);
        Assert.Equal(-50, s.DownRate, 0.5);
        Assert.InRange(estimate.Delay, 0.3, 0.7);
        Assert.True(estimate.Reduction > 0.9);
    }
}
