using FmrFit.Core.Fitting;
using FmrFit.Core.IO;

namespace FmrFit.Core.Tests.Fitting;

public class PeakFitterTests
{
    private static readonly AsymmetricGaussianDerivative Shape = new();

    private static double[] Field() => Enumerable.Range(0, 801).Select(i => 2000 + 10.0 * i).ToArray(); // 2000…10000 Oe

    private static double[] Signal(double[] field, double noise, params double[][] peaks)
    {
        var random = new Random(3);
        return field.Select(h => peaks.Sum(p => Shape.Evaluate(h, p)) + noise * (random.NextDouble() - 0.5)).ToArray();
    }

    [Fact]
    public void Shape_has_extrema_plus_and_minus_A_at_the_two_widths()
    {
        double[] p = [5000, 0.02, 100, 300];

        Assert.Equal(0.02, Shape.Evaluate(4900, p), 12);
        Assert.Equal(-0.02, Shape.Evaluate(5300, p), 12);
        Assert.Equal(0, Shape.Evaluate(5000, p), 12);
        // Extremum: values next to it are smaller in magnitude.
        Assert.True(Shape.Evaluate(4890, p) < 0.02 && Shape.Evaluate(4910, p) < 0.02);
        Assert.True(Shape.Evaluate(5290, p) > -0.02 && Shape.Evaluate(5310, p) > -0.02);
    }

    [Fact]
    public void Markers_are_the_centre_and_the_extrema_at_the_two_widths()
    {
        double[] p = [5000, 0.02, 100, -300]; // widths are used as absolute values

        var markers = Shape.Markers(p)!.Value;

        Assert.Equal(new PeakMarkers(4900, 5000, 5300), markers);
        Assert.Equal(0.02, Shape.Evaluate(markers.Left, p), 12);
        Assert.Equal(-0.02, Shape.Evaluate(markers.Right, p), 12);
    }

    [Fact]
    public void Guess_finds_centre_amplitude_and_width()
    {
        var field = Field();
        var signal = Signal(field, 0, [5000, 0.02, 150, 150]);

        var guess = Shape.Guess(field, signal);

        Assert.Equal(5000, guess[0], 10.0);
        Assert.Equal(0.02, guess[1], 1e-4);
        Assert.Equal(150, guess[2], 10.0);
    }

    [Fact]
    public void Guess_gives_negative_amplitude_for_an_inverted_peak()
    {
        var field = Field();

        Assert.True(Shape.Guess(field, Signal(field, 0, [5000, -0.02, 150, 150]))[1] < 0);
    }

    [Fact]
    public void Fit_recovers_one_asymmetric_peak_from_a_rough_start()
    {
        var field = Field();
        double[] truth = [5000, 0.02, 120, 260];
        var signal = Signal(field, 1e-4, truth);

        var result = PeakFitter.Fit(field, signal, [new Peak(Shape, [5100, 0.015, 200, 200])]);

        var p = result.Peaks[0].Parameters;
        Assert.Equal(truth[0], p[0], 2.0);
        Assert.Equal(truth[1], p[1], 2e-4);
        Assert.Equal(truth[2], Math.Abs(p[2]), 3.0);
        Assert.Equal(truth[3], Math.Abs(p[3]), 3.0);
        Assert.NotNull(result.Peaks[0].Errors);
        Assert.True(result.Iterations > 0);
        // Uniform noise of width 1e-4 has standard deviation 1e-4/√12.
        Assert.Equal(1e-4 / Math.Sqrt(12), result.StandardDeviation, 5e-6);
    }

    [Fact]
    public void Adding_peaks_on_the_residual_and_fitting_recovers_two_peaks()
    {
        var field = Field();
        double[] big = [4500, 0.03, 100, 150];
        double[] small = [7500, -0.01, 200, 120];
        var signal = Signal(field, 0, big, small);

        var peaks = new List<Peak> { PeakFitter.GuessPeak(Shape, field, signal, []) };
        peaks.Add(PeakFitter.GuessPeak(Shape, field, signal, peaks));
        var result = PeakFitter.Fit(field, signal, peaks);

        var fitted = result.Peaks.OrderBy(p => p.Parameters[0]).ToArray();
        Assert.Equal(4500, fitted[0].Parameters[0], 1.0);
        Assert.Equal(7500, fitted[1].Parameters[0], 1.0);
        Assert.Equal(-0.01, fitted[1].Parameters[1], 1e-4);
        Assert.True(result.StandardDeviation < 1e-6);
    }

    [Fact]
    public void FitPeak_fits_only_the_selected_peak_and_locks_the_others()
    {
        var field = Field();
        double[] big = [4500, 0.03, 100, 150];
        double[] small = [7500, -0.01, 200, 120];
        var signal = Signal(field, 0, big, small);
        // Peak 0 is deliberately off and has errors from an earlier fit; peak 1 starts near its true values.
        var locked = new Peak(Shape, [4550, 0.025, 120, 120], Errors: [1, 2, 3, 4]);
        var free = new Peak(Shape, [7450, -0.008, 150, 150]);

        var result = PeakFitter.FitPeak(field, signal, [locked, free], 1);

        Assert.Same(locked, result.Peaks[0]);
        var p = result.Peaks[1].Parameters;
        // Peak 1 sits far from the (wrong) peak 0, so it is still recovered well.
        Assert.Equal(7500, p[0], 1.0);
        Assert.Equal(-0.01, p[1], 2e-4);
        Assert.NotNull(result.Peaks[1].Errors);
        Assert.True(result.Iterations > 0);
        Assert.Equal(signal.Select((v, i) => v - result.Fit[i]), result.Residual);
    }

    [Fact]
    public void FitPeak_respects_the_selected_peaks_limits()
    {
        var field = Field();
        var signal = Signal(field, 0, [5000, 0.02, 120, 260]);
        var start = new Peak(Shape, [5000, 0.005, 150, 150], Limits: [ParameterRange.Unbounded, new(-0.01, 0.01), new(0, 1000), new(0, 1000)]);

        var p = PeakFitter.FitPeak(field, signal, [start], 0).Peaks[0].Parameters;

        Assert.InRange(p[1], -0.01, 0.01);
    }

    [Fact]
    public void FitPeak_rejects_an_index_outside_the_list()
    {
        var field = Field();
        var signal = Signal(field, 0, [5000, 0.02, 100, 100]);
        Peak[] peaks = [new(Shape, [5000, 0.02, 100, 100])];

        Assert.Throws<ArgumentOutOfRangeException>(() => PeakFitter.FitPeak(field, signal, peaks, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PeakFitter.FitPeak(field, signal, peaks, -1));
    }

    [Fact]
    public void Fit_leaves_fixed_parameters_unchanged_and_fits_the_rest()
    {
        var field = Field();
        double[] truth = [5000, 0.02, 120, 260];
        var signal = Signal(field, 1e-5, truth);
        var start = new Peak(Shape, [5000, 0.015, 200, 200]).WithFixed(0, true);

        var fitted = PeakFitter.Fit(field, signal, [start]).Peaks[0];

        Assert.Equal(5000, fitted.Parameters[0]); // exactly
        Assert.Equal(truth[1], fitted.Parameters[1], 1e-4);
        Assert.Equal(truth[2], fitted.Parameters[2], 2.0);
        Assert.Equal(truth[3], fitted.Parameters[3], 2.0);
        Assert.True(double.IsNaN(fitted.Errors![0]));
        Assert.True(double.IsFinite(fitted.Errors[1]) && fitted.Errors[1] > 0);
        Assert.True(fitted.IsFixed(0));
    }

    [Fact]
    public void Fit_keeps_a_wrong_fixed_value_and_compensates_with_the_others()
    {
        var field = Field();
        var signal = Signal(field, 0, [5000, 0.02, 120, 260]);
        var start = new Peak(Shape, [5000, 0.02, 80, 260]).WithFixed(2, true); // true σL is 120

        var fitted = PeakFitter.Fit(field, signal, [start]).Peaks[0];

        Assert.Equal(80, fitted.Parameters[2]);
        Assert.NotEqual(5000, fitted.Parameters[0]); // the free centre moves to make up for the wrong σL
    }

    [Fact]
    public void Fit_with_every_parameter_fixed_only_evaluates()
    {
        var field = Field();
        var signal = Signal(field, 0, [5000, 0.02, 120, 260]);
        var start = new Peak(Shape, [5100, 0.015, 200, 200]).WithAllFixed(true);

        var result = PeakFitter.Fit(field, signal, [start]);

        Assert.Same(start, result.Peaks[0]);
        Assert.Equal(0, result.Iterations);
    }

    [Fact]
    public void FitPeak_respects_fixed_parameters_of_the_selected_peak()
    {
        var field = Field();
        var signal = Signal(field, 0, [4500, 0.03, 100, 150], [7500, -0.01, 200, 120]);
        var other = new Peak(Shape, [4500, 0.03, 100, 150]);
        var selected = new Peak(Shape, [7450, -0.008, 150, 150]).WithFixed(1, true);

        var fitted = PeakFitter.FitPeak(field, signal, [other, selected], 1).Peaks[1];

        Assert.Equal(-0.008, fitted.Parameters[1]);
        Assert.Equal(7500, fitted.Parameters[0], 5.0);
    }

    [Fact]
    public void Fixing_flags_are_kept_through_edits_and_switch_together()
    {
        var peak = new Peak(Shape, [5000, 0.02, 100, 100], Errors: [1, 2, 3, 4]);

        var one = peak.WithFixed(1, true);
        var edited = one.WithParameter(0, 5100);
        var all = peak.WithAllFixed(true);

        Assert.False(peak.IsFixed(1));
        Assert.True(one.IsFixed(1));
        Assert.False(one.IsFixed(0));
        Assert.Equal(peak.Errors, one.Errors); // fixing doesn't change values or errors
        Assert.True(edited.IsFixed(1));
        Assert.True(all.AllFixed);
        Assert.False(one.AllFixed);
        Assert.False(all.WithFixed(3, false).AllFixed);
    }

    [Fact]
    public void Chi_squared_reduced_chi_squared_and_r_squared_follow_their_definitions()
    {
        var field = Field();
        var signal = Signal(field, 1e-3, [5000, 0.02, 100, 100]);
        var peak = new Peak(Shape, [5010, 0.018, 110, 95]).WithFixed(0, true); // 3 free parameters

        var result = PeakFitter.Evaluate(field, signal, [peak]);

        var chi2 = result.Residual.Sum(r => r * r);
        var mean = signal.Average();
        Assert.Equal(field.Length, result.Points);
        Assert.Equal(3, result.FreeParameters);
        Assert.Equal(chi2, result.ChiSquared, 15);
        Assert.Equal(chi2 / (field.Length - 3), result.ReducedChiSquared, 15);
        Assert.Equal(1 - chi2 / signal.Sum(v => (v - mean) * (v - mean)), result.RSquared, 12);
        Assert.Equal(Math.Sqrt(result.ReducedChiSquared), result.StandardDeviation);
    }

    [Fact]
    public void A_perfect_fit_has_zero_chi_squared_and_r_squared_one()
    {
        var field = Field();
        double[] truth = [5000, 0.02, 100, 100];
        var signal = Signal(field, 0, truth);

        var result = PeakFitter.Evaluate(field, signal, [new Peak(Shape, truth)]);

        Assert.Equal(0, result.ChiSquared, 15);
        Assert.Equal(1, result.RSquared, 12);
    }

    [Fact]
    public void Reduced_chi_squared_is_undefined_without_degrees_of_freedom()
    {
        double[] field = [1, 2, 3];
        double[] signal = [0, 1, 0];

        var result = PeakFitter.Evaluate(field, signal, [new Peak(Shape, [2, 1, 1, 1])]);

        Assert.True(double.IsNaN(result.ReducedChiSquared));
        Assert.True(double.IsNaN(result.StandardDeviation));
    }

    [Fact]
    public void Standard_deviation_counts_only_free_parameters()
    {
        var field = Field();
        var signal = Signal(field, 1e-3, [5000, 0.02, 100, 100]);
        var peak = new Peak(Shape, [5000, 0.02, 100, 100]);

        var free = PeakFitter.Evaluate(field, signal, [peak]);
        var fixedPeak = PeakFitter.Evaluate(field, signal, [peak.WithAllFixed(true)]);

        var sumOfSquares = free.Residual.Sum(r => r * r);
        Assert.Equal(Math.Sqrt(sumOfSquares / (field.Length - 4)), free.StandardDeviation, 12);
        Assert.Equal(Math.Sqrt(sumOfSquares / field.Length), fixedPeak.StandardDeviation, 12);
    }

    [Fact]
    public void Evaluate_without_peaks_leaves_the_data_as_residual()
    {
        var field = Field();
        var signal = Signal(field, 0, [5000, 0.02, 100, 100]);

        var result = PeakFitter.Evaluate(field, signal, []);

        Assert.Equal(signal, result.Residual);
        Assert.All(result.Fit, v => Assert.Equal(0, v));
    }

    [Fact]
    public void Fits_a_real_work_file_resonance()
    {
        // 42 GHz sample: resonance near 4.8 kOe (see the phase tests).
        var m = FmrCsvReader.Read(Path.Combine(AppContext.BaseDirectory, "data", "MM100610_cprllH_42G.csv"));
        var field = m.Field.Where((_, i) => i % 3 == 0).ToArray();
        var signal = m.X.Where((_, i) => i % 3 == 0).Select(v => v * 1000).ToArray();

        var peaks = new List<Peak> { PeakFitter.GuessPeak(Shape, field, signal, []) };
        var before = PeakFitter.Evaluate(field, signal, peaks).StandardDeviation;
        var result = PeakFitter.Fit(field, signal, peaks);

        Assert.InRange(result.Peaks[0].Parameters[0], 4500, 5100);
        Assert.True(result.StandardDeviation < before);
    }

    [Fact]
    public void Fit_stops_after_the_given_iterations()
    {
        var field = Field();
        var signal = Signal(field, 1e-4, [5000, 0.02, 120, 260]);
        Peak[] start = [new Peak(Shape, [5100, 0.015, 200, 200])];

        var full = PeakFitter.Fit(field, signal, start);
        var capped = PeakFitter.Fit(field, signal, start, maxIterations: 3);
        var cappedSingle = PeakFitter.FitPeak(field, signal, start, 0, maxIterations: 3);

        Assert.True(full.Iterations > 3, $"needs more than 3 iterations, took {full.Iterations}");
        Assert.InRange(capped.Iterations, 1, 3);
        Assert.InRange(cappedSingle.Iterations, 1, 3);
    }

    [Fact]
    public void GuessPeak_sets_default_limits_with_the_given_factor()
    {
        var field = Field();
        var signal = Signal(field, 0, [5000, 0.02, 150, 150]);

        var peak = PeakFitter.GuessPeak(Shape, field, signal, [], limitFactor: 3);

        // Gaussian [H0, A, σL, σR]: amplitude ±3|A|, widths up to 3 × the larger width.
        var largestWidth = Math.Max(Math.Abs(peak.Parameters[2]), Math.Abs(peak.Parameters[3]));
        Assert.Equal(3 * Math.Abs(peak.Parameters[1]), peak.Range(1).Max, 12);
        Assert.Equal(3 * largestWidth, peak.Range(2).Max, 9);
        Assert.Equal(3 * largestWidth, peak.Range(3).Max, 9);
    }
}
