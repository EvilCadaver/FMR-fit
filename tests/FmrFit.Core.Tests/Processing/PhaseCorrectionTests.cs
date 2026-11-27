using FmrFit.Core.IO;
using FmrFit.Core.Processing;
using MathNet.Numerics;

namespace FmrFit.Core.Tests.Processing;

public class PhaseCorrectionTests
{
    /// <summary>
    /// Absorption-like peak entirely in X; Y is a constant plus a linear background in field of slope
    /// <paramref name="ySlope"/> per Oe. X also has a constant offset.
    /// </summary>
    private static (double[] Field, double[] X, double[] Y) PeakInX(double ySlope = 0)
    {
        const int n = 500;
        var field = new double[n];
        var x = new double[n];
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            field[i] = 1000 + 2 * i;
            var u = (i - 250) / 20.0;
            x[i] = 1 / (1 + u * u) + 0.3;
            y[i] = -0.2 + ySlope * (field[i] - 1000);
        }
        return (field, x, y);
    }

    [Fact]
    public void Rotation_follows_lock_in_convention()
    {
        var (x, y) = PhaseCorrection.Rotate([1.0], [0.0], 90);

        Assert.Equal(0, x[0], 12);
        Assert.Equal(-1, y[0], 12);
    }

    [Fact]
    public void Rotation_keeps_magnitude_and_is_undone_by_opposite_angle()
    {
        var (_, x0, y0) = PeakInX();
        var (x1, y1) = PhaseCorrection.Rotate(x0, y0, 37);
        var (x2, y2) = PhaseCorrection.Rotate(x1, y1, -37);

        for (var i = 0; i < x0.Length; i++)
        {
            Assert.Equal(Math.Sqrt(x0[i] * x0[i] + y0[i] * y0[i]), Math.Sqrt(x1[i] * x1[i] + y1[i] * y1[i]), 12);
            Assert.Equal(x0[i], x2[i], 12);
            Assert.Equal(y0[i], y2[i], 12);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(-60)]
    [InlineData(89)]
    public void Estimate_undoes_a_known_phase_error(double error)
    {
        var (field, x0, y0) = PeakInX();
        var (x, y) = PhaseCorrection.Rotate(x0, y0, -error);

        var delta = PhaseCorrection.EstimateMinYVariance(field, x, y);

        Assert.Equal(error, delta, 6);
        var (_, yc) = PhaseCorrection.Rotate(x, y, delta);
        Assert.True(yc.Max() - yc.Min() < 1e-9, "Y should be flat after correction");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-40)]
    public void Linear_background_in_Y_does_not_bias_the_estimate(double error)
    {
        // A strong sloped background in Y; plain variance minimization would tilt the phase to reduce it.
        var (field, x0, y0) = PeakInX(ySlope: 1e-3);
        var (x, y) = PhaseCorrection.Rotate(x0, y0, -error);

        Assert.Equal(error, PhaseCorrection.EstimateMinYVariance(field, x, y), 6);
    }

    [Fact]
    public void Background_is_not_removed_from_the_data()
    {
        var (field, x, y) = PeakInX(ySlope: 1e-3);
        var xCopy = x.ToArray();
        var yCopy = y.ToArray();

        PhaseCorrection.EstimateMinYVariance(field, x, y);

        Assert.Equal(xCopy, x);
        Assert.Equal(yCopy, y);
    }

    /// <summary>
    /// Up/down sweep at ±50 Oe/s whose signal lags the field by <paramref name="delay"/>: peak in X at 2500 Oe,
    /// linear background in Y (both in the true, lagged field), optionally a step at 1300 Oe with its own phase
    /// (like low-field absorption), then a phase error <paramref name="phaseError"/> applied.
    /// </summary>
    private static FmrMeasurement LaggedSweep(double delay, double phaseError, bool lowFieldStep = false)
    {
        const double rate = 50, start = 1000, top = 4000, dt = 0.2;
        var turn = (top - start) / rate;
        double FieldAt(double t) => t <= turn ? start + rate * t : top - rate * (t - turn);

        var time = new List<double>();
        var field = new List<double>();
        var x = new List<double>();
        var y = new List<double>();
        for (var t = 0.0; t <= 2 * turn; t += dt)
        {
            var lagged = FieldAt(Math.Max(0, t - delay));
            var u = (lagged - 2500) / 40;
            time.Add(t);
            field.Add(FieldAt(t));
            var step = lowFieldStep ? Math.Tanh((lagged - 1300) / 30) : 0;
            x.Add(1 / (1 + u * u) + 0.3 + 0.3 * step);
            y.Add(-0.2 + 1e-3 * (lagged - start) + 0.2 * step);
        }

        var (rx, ry) = PhaseCorrection.Rotate([.. x], [.. y], -phaseError);
        return new FmrMeasurement(FmrMetadata.Empty, [.. time], [.. field], rx, ry, 0);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(4.0)]
    [InlineData(-25.0)]
    public void Sweep_estimate_uses_the_field_corrected_data(double phaseError)
    {
        const double delay = 0.5;
        var sweeps = FieldSweeps.FromMeasurement(LaggedSweep(delay, phaseError));
        var correction = SweepLagCorrection.FieldCorrectionFor(sweeps, delay); // 25 Oe

        var withCorrection = PhaseCorrection.EstimateAtResonance(sweeps, correction);
        var withoutCorrection = PhaseCorrection.EstimateAtResonance(sweeps, 0);

        Assert.Equal(phaseError, withCorrection, 0.02);
        Assert.True(Math.Abs(withCorrection - phaseError) < Math.Abs(withoutCorrection - phaseError),
            $"with correction {withCorrection}, without {withoutCorrection}");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(4.0)]
    [InlineData(-25.0)]
    public void Feature_away_from_resonance_does_not_pull_the_estimate(double phaseError)
    {
        const double delay = 0.5;
        var m = LaggedSweep(delay, phaseError, lowFieldStep: true);
        var sweeps = FieldSweeps.FromMeasurement(m);
        var correction = SweepLagCorrection.FieldCorrectionFor(sweeps, delay);

        var atResonance = PhaseCorrection.EstimateAtResonance(sweeps, correction);
        var wholeRange = PhaseCorrection.EstimateMinYVariance(m.Field, m.X, m.Y);

        Assert.Equal(phaseError, atResonance, 0.05);
        Assert.True(Math.Abs(wholeRange - phaseError) > 1, $"whole-range estimate {wholeRange} should be pulled by the step");
    }

    [Fact]
    public void Resonance_window_is_found_at_the_peak_not_the_step()
    {
        var sweeps = FieldSweeps.FromMeasurement(LaggedSweep(0, 0, lowFieldStep: true));
        double[] field = [.. sweeps.Up.Field, .. sweeps.Down.Field];

        var window = ResonanceWindow.Find(field, [.. sweeps.Up.X, .. sweeps.Down.X], [.. sweeps.Up.Y, .. sweeps.Down.Y]);

        Assert.NotNull(window);
        Assert.InRange(window.Center, 2400, 2600);
        Assert.InRange(window.HalfWidth, 5, 200);
        Assert.False(window.Contains(1300, PhaseCorrection.ResonanceWindowWidths));
    }

    [Fact]
    public void Sample_42GHz_estimate_matches_visual_check()
    {
        // Judged by eye: at 97.4° Y is flat through the resonance (~4.8 kOe); the whole-range estimate gave 97.6°.
        var m = FmrCsvReader.Read(Path.Combine(AppContext.BaseDirectory, "data", "MM100610_cprllH_42G.csv"));
        var sweeps = FieldSweeps.FromMeasurement(m);
        var correction = Math.Round(SweepLagCorrection.Estimate(sweeps).FieldCorrection, 1);

        var phase = m.Metadata.LockInPhaseDeg!.Value + PhaseCorrection.EstimateAtResonance(sweeps, correction);

        Assert.Equal(97.4, phase, 0.1);
    }

    [Fact]
    public void Estimate_is_within_plus_minus_90_degrees()
    {
        var (field, x0, y0) = PeakInX();
        var (x, y) = PhaseCorrection.Rotate(x0, y0, -150); // phase error of +150°, same axis as −30°

        Assert.Equal(-30, PhaseCorrection.EstimateMinYVariance(field, x, y), 6);
    }

    [Theory]
    [InlineData(190, -170)]
    [InlineData(-180, 180)]
    [InlineData(540, 180)]
    [InlineData(45, 45)]
    public void Normalize_wraps_to_half_open_range(double input, double expected)
    {
        Assert.Equal(expected, PhaseCorrection.Normalize(input), 12);
    }

    [Fact]
    public void Sample_file_estimate_minimizes_Y_variation_around_a_linear_background()
    {
        var m = FmrCsvReader.Read(Path.Combine(AppContext.BaseDirectory, "data", "MM100610_cprllH_42G.csv"));

        var delta = PhaseCorrection.EstimateMinYVariance(m.Field, m.X, m.Y);

        double ResidualYVariance(double d)
        {
            var (_, y) = PhaseCorrection.Rotate(m.X, m.Y, d);
            var (a, b) = Fit.Line(m.Field, y);
            return y.Select((v, i) => v - (a + b * m.Field[i])).Sum(r => r * r);
        }

        Assert.True(ResidualYVariance(delta) <= ResidualYVariance(delta + 0.5));
        Assert.True(ResidualYVariance(delta) <= ResidualYVariance(delta - 0.5));
    }
}
