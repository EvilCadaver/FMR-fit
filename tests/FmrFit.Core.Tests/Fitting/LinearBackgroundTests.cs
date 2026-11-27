using FmrFit.Core.Fitting;
using FmrFit.Core.IO;

namespace FmrFit.Core.Tests.Fitting;

public class LinearBackgroundTests
{
    private static readonly LinearBackground Background = new();
    private static readonly LorentzianDerivative Lorentzian = new();

    private static double[] Field() => Enumerable.Range(0, 801).Select(i => 2000 + 10.0 * i).ToArray(); // 2000…10000 Oe

    [Fact]
    public void Is_offered_with_its_parameters_units_and_formula()
    {
        Assert.Same(Background.GetType(), PeakShapes.ById("LinearBackground")!.GetType());
        Assert.Equal(["c", "s"], Background.ParameterNames);
        Assert.Equal(["mV", "mV/Oe"], Background.ParameterKinds.Select(ParameterUnits.Of));
        Assert.Equal(["mV", "mV/Oe"], Background.ParameterKinds.Select(ParameterUnits.Ascii));
        Assert.Equal("f(H) = c + s*H", Background.Formula);
        Assert.Null(Background.Markers([1, 2]));
        Assert.False(PeakScaling.Applies(Background));
    }

    [Fact]
    public void Evaluates_a_straight_line()
    {
        Assert.Equal(0.3 + 2e-5 * 5000, Background.Evaluate(5000, [0.3, 2e-5]), 15);
        Assert.Equal(0.3, Background.Evaluate(0, [0.3, 2e-5]));
    }

    [Fact]
    public void Guess_is_the_least_squares_line_through_the_residual()
    {
        var field = Field();
        var residual = field.Select(h => -0.12 + 3.5e-5 * h).ToArray();

        var guess = Background.Guess(field, residual);

        Assert.Equal(-0.12, guess[0], 12);
        Assert.Equal(3.5e-5, guess[1], 15);
        Assert.Equal([0.0, 0.0], Background.Guess([], []));
    }

    [Fact]
    public void Default_limits_follow_the_data_even_for_a_flat_guess()
    {
        var field = Field();
        var residual = field.Select(h => 0.5 * Math.Sin(h / 300)).ToArray(); // no trend, amplitude 0.5

        var peak = PeakFitter.GuessPeak(Background, field, residual, []);

        // ≈ 10 × 0.5 for the offset and 10 × 0.5 / 8000 Oe for the slope, plus twice the (small) guess.
        Assert.InRange(peak.Range(0).Max, 5, 5.2);
        Assert.Equal(-peak.Range(0).Max, peak.Range(0).Min);
        Assert.InRange(peak.Range(1).Max, 6.25e-4, 6.5e-4);
        Assert.True(peak.Range(0).Contains(peak.Parameters[0]) && peak.Range(1).Contains(peak.Parameters[1]));
    }

    [Fact]
    public void Fits_a_peak_on_a_tilted_offset_baseline()
    {
        var field = Field();
        double[] line = [5000, 300, 600, 150];
        double[] baseline = [-0.004, 1.5e-6];
        var random = new Random(9);
        var signal = field.Select(h => Lorentzian.Evaluate(h, line) + Background.Evaluate(h, baseline)
            + 1e-5 * (random.NextDouble() - 0.5)).ToArray();

        var peaks = new List<Peak> { PeakFitter.GuessPeak(Lorentzian, field, signal, []) };
        peaks.Add(PeakFitter.GuessPeak(Background, field, signal, peaks));
        var result = PeakFitter.Fit(field, signal, peaks);

        var p = result.Peaks[0].Parameters;
        var b = result.Peaks[1].Parameters;
        Assert.Equal(5000, p[0], 1.0);
        Assert.Equal(300, Math.Abs(p[1]), 1.0);
        Assert.Equal(600, p[2], 3.0);
        Assert.Equal(-0.004, b[0], 1e-5);
        Assert.Equal(1.5e-6, b[1], 2e-9);
        Assert.True(result.RSquared > 0.999);
        Assert.All(result.Peaks[1].Errors!, e => Assert.True(double.IsFinite(e) && e > 0));
    }

    [Fact]
    public void Absorbs_a_raised_baseline_inside_a_fit_range()
    {
        // FMR line on a plateau that is raised over the whole fit range (as between FMAR and FMR).
        var field = Field();
        double[] line = [5700, 600, 1e6, -2e5];
        var signal = field.Select(h => Lorentzian.Evaluate(h, line) + (h < 6500 ? 0.3 : 0.3 - (h - 6500) * 1e-4)).ToArray();
        var (f, s) = new FieldRange(4500, 6500).Slice(field, signal);

        var alone = PeakFitter.Fit(f, s, [PeakFitter.GuessPeak(Lorentzian, f, s, [])]);
        var peaks = new List<Peak> { PeakFitter.GuessPeak(Lorentzian, f, s, []) };
        peaks.Add(PeakFitter.GuessPeak(Background, f, s, peaks));
        var withBackground = PeakFitter.Fit(f, s, peaks);

        Assert.Equal(5700, withBackground.Peaks[0].Parameters[0], 1.0);
        Assert.Equal(0.3, withBackground.Peaks[1].Parameters[0], 1e-3);
        Assert.True(withBackground.ChiSquared < alone.ChiSquared / 100);
    }

    [Fact]
    public void Survives_saving_with_its_formula_and_units()
    {
        var field = Field();
        var signal = field.Select(h => Lorentzian.Evaluate(h, [5000, 300, 600, 0]) + 0.01).ToArray();
        var peaks = new List<Peak> { PeakFitter.GuessPeak(Lorentzian, field, signal, []) };
        peaks.Add(PeakFitter.GuessPeak(Background, field, signal, peaks));
        var result = PeakFitter.Fit(field, signal, peaks);

        var file = FitFileConversion.ToFitFile("x.csv", result.Peaks, result, DateTimeOffset.Now);
        using var writer = new StringWriter();
        FitFileWriter.Write(writer, file);
        var text = writer.ToString();
        var back = FitFileConversion.ToPeaks(FitFileReader.Read(new StringReader(text)));

        Assert.Contains("# LinearBackground = f(H) = c + s*H\n", text);
        Assert.Contains("\n2,s,", text);
        Assert.Contains(",mV/Oe,", text);
        Assert.Equal(result.Peaks[1].Parameters, back[1].Parameters);
        Assert.IsType<LinearBackground>(back[1].Shape);
    }
}
