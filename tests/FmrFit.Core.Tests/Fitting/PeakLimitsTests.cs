using FmrFit.Core.Fitting;
using FmrFit.Core.IO;

namespace FmrFit.Core.Tests.Fitting;

public class PeakLimitsTests
{
    private static readonly AsymmetricGaussianDerivative Gaussian = new();
    private static readonly LorentzianDerivative Lorentzian = new();

    private static double[] Field() => Enumerable.Range(0, 801).Select(i => 2000 + 10.0 * i).ToArray(); // 2000…10000 Oe

    /// <summary>Gaussian [H0, A, σL, σR] ranges: position [min, max], amplitude ±a, widths [MinWidth, w].</summary>
    private static ParameterRange[] GaussianLimits(double min, double max, double a, double w) =>
        [new(min, max), new(-a, a), new(PeakLimits.MinWidth, w), new(PeakLimits.MinWidth, w)];

    [Fact]
    public void Default_is_ten_times_the_largest_value_of_the_same_kind()
    {
        // Gaussian [H0, A, σL, σR]: both widths share the larger one's limit.
        var limits = PeakLimits.Default(Gaussian, [5000, -0.02, 100, 300]);

        Assert.Equal(ParameterRange.Unbounded, limits[0]);
        Assert.Equal(-0.2, limits[1].Min, 12);
        Assert.Equal(0.2, limits[1].Max, 12);
        Assert.Equal(new ParameterRange(PeakLimits.MinWidth, 3000), limits[2]);
        Assert.Equal(new ParameterRange(PeakLimits.MinWidth, 3000), limits[3]);
    }

    [Fact]
    public void Default_uses_the_given_factor()
    {
        var limits = PeakLimits.Default(Gaussian, [5000, -0.02, 100, 300], factor: 4);

        Assert.Equal(new ParameterRange(-0.08, 0.08), limits[1]);
        Assert.Equal(new ParameterRange(PeakLimits.MinWidth, 1200), limits[2]);
        Assert.Equal(new ParameterRange(PeakLimits.MinWidth, 1200), limits[3]);
    }

    [Fact]
    public void Default_limits_the_position_to_the_given_field_range()
    {
        Assert.Equal(new ParameterRange(2000, 10000), PeakLimits.Default(Gaussian, [5000, 0.02, 100, 100], 2000, 10000)[0]);
    }

    [Fact]
    public void Default_gives_a_zero_amplitude_the_limit_of_the_other_amplitude()
    {
        // Lorentzian [xc, w, A1, A2] with A2 = 0, as from Guess.
        var limits = PeakLimits.Default(Lorentzian, [5000, 200, -4e4, 0]);

        Assert.Equal(new ParameterRange(PeakLimits.MinWidth, 2000), limits[1]);
        Assert.Equal(new ParameterRange(-4e5, 4e5), limits[2]);
        Assert.Equal(new ParameterRange(-4e5, 4e5), limits[3]);
    }

    [Fact]
    public void Default_falls_back_to_one_when_all_values_of_a_kind_are_zero()
    {
        Assert.Equal(new ParameterRange(-1, 1), PeakLimits.Default(Lorentzian, [5000, 200, 0, 0])[2]);
    }

    [Fact]
    public void A_single_limit_gives_min_width_to_limit_for_widths_and_symmetric_for_amplitudes()
    {
        Assert.Equal(new ParameterRange(PeakLimits.MinWidth, 50), PeakLimits.FromLimit(ParameterKind.Width, 50));
        Assert.Equal(new ParameterRange(-2, 2), PeakLimits.FromLimit(ParameterKind.Amplitude, 2));
        Assert.Equal(new ParameterRange(-3, 3), PeakLimits.FromLimit(ParameterKind.AmplitudeFieldSquared, 3));
        Assert.Throws<ArgumentException>(() => PeakLimits.FromLimit(ParameterKind.Position, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => PeakLimits.FromLimit(ParameterKind.Width, 0));
    }

    [Fact]
    public void WithParameter_sets_the_value_clamps_it_and_drops_errors()
    {
        var peak = new Peak(Gaussian, [5000, 0.02, 100, 100], Errors: [1, 1, 1, 1], Limits: GaussianLimits(4000, 6000, 0.05, 400));

        var moved = peak.WithParameter(0, 5500);
        var outside = peak.WithParameter(0, 12345);
        var tooBig = peak.WithParameter(1, -0.3);
        var negativeWidth = peak.WithParameter(2, -150);

        Assert.Equal(5500, moved.Parameters[0]);
        Assert.Null(moved.Errors);
        Assert.Equal(6000, outside.Parameters[0]);
        Assert.Equal(-0.05, tooBig.Parameters[1]);
        Assert.Equal(150, negativeWidth.Parameters[2]);
        Assert.Equal([5000, 0.02, 100, 100], peak.Parameters); // the original is unchanged
    }

    [Fact]
    public void WithParameter_on_a_peak_without_limits_uses_and_stores_the_defaults()
    {
        var peak = new Peak(Gaussian, [5000, 0.02, 100, 100]);

        var changed = peak.WithParameter(1, 1.0);

        Assert.Equal(0.2, changed.Parameters[1], 12);
        Assert.NotNull(changed.Limits);
        Assert.Equal(0.2, changed.Limits![1].Max, 12);
    }

    [Fact]
    public void WithLimit_clamps_a_value_outside_the_new_range()
    {
        var peak = new Peak(Gaussian, [5000, -0.02, 100, 300], Errors: [1, 1, 1, 1], Limits: GaussianLimits(2000, 10000, 0.1, 1000));

        var narrower = peak.WithLimit(1, 0.01).WithLimit(3, 200);
        var wider = peak.WithLimit(2, 5000);

        Assert.Equal(-0.01, narrower.Parameters[1]);
        Assert.Equal(200, narrower.Parameters[3]);
        Assert.Equal(new ParameterRange(-0.01, 0.01), narrower.Limits![1]);
        Assert.Equal(new ParameterRange(PeakLimits.MinWidth, 5000), wider.Limits![2]);
        Assert.Equal(peak.Parameters, wider.Parameters);
        Assert.NotNull(wider.Errors); // nothing changed, so the errors still apply
    }

    [Fact]
    public void WithLimit_rejects_positions_and_non_positive_limits()
    {
        var peak = new Peak(Gaussian, [5000, 0.02, 100, 100]);

        Assert.Throws<ArgumentException>(() => peak.WithLimit(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => peak.WithLimit(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => peak.WithLimit(1, double.NaN));
    }

    [Fact]
    public void Position_minimum_and_maximum_clamp_the_position()
    {
        var peak = new Peak(Gaussian, [5000, 0.02, 100, 100], Errors: [1, 1, 1, 1], Limits: GaussianLimits(2000, 10000, 1, 1000));

        var raisedMin = peak.WithMinimum(0, 5200);
        var loweredMax = peak.WithMaximum(0, 4800);
        var unaffected = peak.WithMinimum(0, 3000).WithMaximum(0, 7000);

        Assert.Equal(new ParameterRange(5200, 10000), raisedMin.Limits![0]);
        Assert.Equal(5200, raisedMin.Parameters[0]);
        Assert.Null(raisedMin.Errors);
        Assert.Equal(new ParameterRange(2000, 4800), loweredMax.Limits![0]);
        Assert.Equal(4800, loweredMax.Parameters[0]);
        Assert.Equal(new ParameterRange(3000, 7000), unaffected.Limits![0]);
        Assert.Equal(5000, unaffected.Parameters[0]);
        Assert.NotNull(unaffected.Errors);
    }

    [Fact]
    public void Minimum_never_exceeds_maximum_and_vice_versa()
    {
        var peak = new Peak(Gaussian, [5000, 0.02, 100, 100], Limits: GaussianLimits(4000, 6000, 1, 1000));

        var minTooHigh = peak.WithMinimum(0, 7000);
        var maxTooLow = peak.WithMaximum(0, 3000);

        Assert.Equal(new ParameterRange(6000, 6000), minTooHigh.Limits![0]);
        Assert.Equal(6000, minTooHigh.Parameters[0]);
        Assert.Equal(new ParameterRange(4000, 4000), maxTooLow.Limits![0]);
        Assert.Equal(4000, maxTooLow.Parameters[0]);
    }

    [Fact]
    public void Minimum_and_maximum_must_be_finite()
    {
        var peak = new Peak(Gaussian, [5000, 0.02, 100, 100]);

        Assert.Throws<ArgumentOutOfRangeException>(() => peak.WithMinimum(0, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => peak.WithMaximum(0, double.PositiveInfinity));
    }

    [Fact]
    public void GuessPeak_sets_default_limits_and_fit_keeps_them()
    {
        var field = Field();
        var signal = field.Select(h => Gaussian.Evaluate(h, [5000, 0.02, 120, 260])).ToArray();

        var guess = PeakFitter.GuessPeak(Gaussian, field, signal, []);
        var fitted = PeakFitter.Fit(field, signal, [guess]).Peaks[0];

        Assert.Equal(PeakLimits.Default(Gaussian, guess.Parameters, 2000, 10000), guess.Limits);
        Assert.Equal(guess.Limits, fitted.Limits);
    }

    [Fact]
    public void Fit_keeps_an_amplitude_within_its_limit()
    {
        var field = Field();
        var signal = field.Select(h => Gaussian.Evaluate(h, [5000, 0.02, 120, 260])).ToArray();
        var start = new Peak(Gaussian, [5000, 0.005, 150, 150], Limits: GaussianLimits(2000, 10000, 0.01, 1000));

        var p = PeakFitter.Fit(field, signal, [start]).Peaks[0].Parameters;

        Assert.InRange(p[1], -0.01, 0.01);
        Assert.Equal(0.01, p[1], 1e-6); // pushed against the limit
    }

    [Fact]
    public void Fit_keeps_a_width_within_its_limit()
    {
        var field = Field();
        var signal = field.Select(h => Gaussian.Evaluate(h, [5000, 0.02, 120, 260])).ToArray();
        var start = new Peak(Gaussian, [5000, 0.02, 100, 100], Limits: GaussianLimits(2000, 10000, 1, 200));

        var p = PeakFitter.Fit(field, signal, [start]).Peaks[0].Parameters;

        Assert.InRange(p[3], 0, 200);
        Assert.Equal(200, p[3], 1e-3); // the true σR = 260 is out of range, so it ends at the limit
    }

    [Fact]
    public void Fit_keeps_the_position_between_its_minimum_and_maximum()
    {
        var field = Field();
        var signal = field.Select(h => Gaussian.Evaluate(h, [5000, 0.02, 120, 260])).ToArray();
        var start = new Peak(Gaussian, [4800, 0.02, 150, 150], Limits: GaussianLimits(4700, 4900, 1, 1000));

        var p = PeakFitter.Fit(field, signal, [start]).Peaks[0].Parameters;

        Assert.InRange(p[0], 4700, 4900);
        Assert.Equal(4900, p[0], 1e-3); // the true 5000 is above the maximum
    }

    [Fact]
    public void Fit_holds_a_position_whose_minimum_equals_its_maximum()
    {
        var field = Field();
        var signal = field.Select(h => Gaussian.Evaluate(h, [5000, 0.02, 120, 260])).ToArray();
        var start = new Peak(Gaussian, [4950, 0.015, 150, 150], Limits: GaussianLimits(4950, 4950, 1, 1000));

        var p = PeakFitter.Fit(field, signal, [start]).Peaks[0].Parameters;

        Assert.Equal(4950, p[0], 1e-3);
        Assert.True(double.IsFinite(p[1]) && double.IsFinite(p[2]) && double.IsFinite(p[3]));
    }

    [Fact]
    public void Fit_within_loose_limits_gives_small_finite_errors()
    {
        var field = Field();
        var random = new Random(5);
        var signal = field.Select(h => Gaussian.Evaluate(h, [5000, 0.02, 120, 260]) + 1e-4 * (random.NextDouble() - 0.5)).ToArray();

        var result = PeakFitter.Fit(field, signal, [PeakFitter.GuessPeak(Gaussian, field, signal, [])]);

        var errors = result.Peaks[0].Errors!;
        Assert.All(errors, e => Assert.True(double.IsFinite(e) && e > 0));
        Assert.True(errors[0] < 2, $"xc error {errors[0]}");
        Assert.True(errors[1] < 1e-4, $"A error {errors[1]}");
    }

    [Fact]
    public void Fitted_real_work_file_peaks_stay_within_their_limits()
    {
        var m = FmrCsvReader.Read(Path.Combine(AppContext.BaseDirectory, "data", "MM100610_cprllH_42G.csv"));
        var field = m.Field.Where((_, i) => i % 3 == 0).ToArray();
        var signal = m.X.Where((_, i) => i % 3 == 0).Select(v => v * 1000).ToArray();

        var guess = PeakFitter.GuessPeak(Lorentzian, field, signal, []);
        var fitted = PeakFitter.Fit(field, signal, [guess]).Peaks[0];

        for (var k = 0; k < fitted.Parameters.Length; k++)
            Assert.True(fitted.Range(k).Contains(fitted.Parameters[k]), $"parameter {k} = {fitted.Parameters[k]} outside {fitted.Range(k)}");
        Assert.InRange(fitted.Parameters[0], field.Min(), field.Max());
    }

    [Fact]
    public void A_width_limit_below_the_minimum_width_holds_the_width_at_it()
    {
        Assert.Equal(new ParameterRange(PeakLimits.MinWidth, PeakLimits.MinWidth), PeakLimits.FromLimit(ParameterKind.Width, 0.05));

        var peak = new Peak(Lorentzian, [5000, 200, 4e4, 0]).WithLimit(1, 0.05);

        Assert.Equal(PeakLimits.MinWidth, peak.Parameters[1]);
    }

    [Fact]
    public void Stored_width_ranges_from_zero_are_raised_to_the_minimum_width()
    {
        // As in fit files written before the minimum existed.
        var peak = new Peak(Lorentzian, [5000, 200, 4e4, 0],
            Limits: [new(2000, 10000), new(0, 1000), new(-1e5, 1e5), new(-1e5, 1e5)]);

        Assert.Equal(new ParameterRange(PeakLimits.MinWidth, 1000), peak.Range(1));
        Assert.Equal(new ParameterRange(2000, 10000), peak.Range(0)); // other kinds untouched
        Assert.Equal(new ParameterRange(0, 1000), peak.Limits![1]); // the stored ranges are not modified

        var valid = peak with { Limits = peak.LimitsOrDefault() };
        Assert.Same(valid.Limits, valid.LimitsOrDefault()); // nothing to raise: no copy
    }

    [Fact]
    public void Manual_changes_cannot_set_a_width_below_the_minimum()
    {
        var peak = new Peak(Gaussian, [5000, 0.02, 100, 100], Limits: [new(2000, 10000), new(-1, 1), new(0, 400), new(0, 400)]);

        Assert.Equal(PeakLimits.MinWidth, peak.WithParameter(2, 0).Parameters[2]);
        Assert.Equal(PeakLimits.MinWidth, peak.WithParameter(3, -0.01).Parameters[3]);
        Assert.Equal(PeakLimits.MinWidth, peak.WithMinimum(2, 0).Range(2).Min);
        Assert.Equal(PeakLimits.MinWidth, peak.WithWidthKeepingHeight(2, 0.001).Parameters[2]);
    }

    [Fact]
    public void WithMinimumWidths_raises_only_too_small_widths()
    {
        var narrow = new Peak(Gaussian, [5000, 0.02, 0.01, 100], Errors: [1, 1, 1, 1],
            Limits: [new(2000, 10000), new(-1, 1), new(0, 400), new(0, 400)]);
        var fine = narrow with { Parameters = [5000, 0.02, 50, 100] };

        var raised = narrow.WithMinimumWidths();
        var kept = fine.WithMinimumWidths();

        Assert.Equal([5000, 0.02, PeakLimits.MinWidth, 100], raised.Parameters);
        Assert.Null(raised.Errors);
        Assert.Equal(fine.Parameters, kept.Parameters);
        Assert.NotNull(kept.Errors);
        Assert.Equal(PeakLimits.MinWidth, kept.Limits![2].Min);
    }

    [Fact]
    public void The_fit_keeps_widths_at_or_above_the_minimum()
    {
        // Data with a much narrower line than allowed: the fitted width stops at the minimum.
        var field = Enumerable.Range(0, 2001).Select(i => 4999 + 0.001 * i).ToArray(); // 4999…5001 Oe
        double[] truth = [5000, 0.02, 0.02, 0.02];
        var signal = field.Select(h => Gaussian.Evaluate(h, truth)).ToArray();
        var start = new Peak(Gaussian, [5000, 0.02, 0.5, 0.5], Limits: [new(4999, 5001), new(-1, 1), new(0, 10), new(0, 10)]);

        var fitted = PeakFitter.Fit(field, signal, [start]).Peaks[0];

        Assert.True(Math.Abs(fitted.Parameters[2]) >= PeakLimits.MinWidth);
        Assert.True(Math.Abs(fitted.Parameters[3]) >= PeakLimits.MinWidth);
    }
}
