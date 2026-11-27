using FmrFit.Core.Fitting;
using FmrFit.Core.Processing;

namespace FmrFit.Core.Tests.Fitting;

public class LorentzianAbsorptionTests
{
    private static readonly LorentzianAbsorption Shape = new();
    private static readonly LorentzianDerivative DerivativeShape = new();

    private static double[] Field() => Enumerable.Range(0, 801).Select(i => 2000 + 10.0 * i).ToArray(); // 2000…10000 Oe

    [Theory]
    [InlineData(4700.0)]
    [InlineData(4950.0)]
    [InlineData(5000.0)]
    [InlineData(5263.0)]
    public void Its_field_derivative_is_the_Lorentzian_derivative_with_the_same_parameters(double h)
    {
        double[] p = [5000, 200, 3.0, -1.5];

        var derivative = (Shape.Evaluate(h + 1e-3, p) - Shape.Evaluate(h - 1e-3, p)) / 2e-3;

        Assert.Equal(DerivativeShape.Evaluate(h, p), derivative, 1e-9);
    }

    [Fact]
    public void Matches_the_given_formula()
    {
        double x = 5123, xc = 5000, w = 180, a1 = 2.5, a2 = 0.7;
        var u = x - xc;
        var formula = a1 * w / (2 * (4 * u * u + w * w)) - a2 * u / (4 * u * u + w * w);

        Assert.Equal(formula, Shape.Evaluate(x, [xc, w, a1, a2]), 15);
    }

    [Fact]
    public void Absorption_peaks_at_xc_with_height_A1_over_2w_and_full_width_w()
    {
        const double xc = 5000, w = 300, a1 = 600;

        Assert.Equal(a1 / (2 * w), Shape.Evaluate(xc, [xc, w, a1, 0]), 12);
        Assert.Equal(a1 / (4 * w), Shape.Evaluate(xc - w / 2, [xc, w, a1, 0]), 12);
        Assert.Equal(a1 / (4 * w), Shape.Evaluate(xc + w / 2, [xc, w, a1, 0]), 12);
    }

    [Fact]
    public void Markers_match_the_derivative_shape()
    {
        double[] p = [5000, 300, 1, 0];

        Assert.Equal(DerivativeShape.Markers(p), Shape.Markers(p));
    }

    [Fact]
    public void Zero_width_gives_zero_instead_of_nan()
    {
        Assert.Equal(0, Shape.Evaluate(5000, [5000, 0, 1, 1]));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(-1.0)] // FMAR: a dip
    public void Guess_finds_centre_width_and_height(double sign)
    {
        var field = Field();
        double[] truth = [6000, 400, sign * 800, 0];
        var data = field.Select(h => Shape.Evaluate(h, truth)).ToArray();

        var guess = Shape.Guess(field, data);

        Assert.Equal(6000, guess[0], 6);
        Assert.Equal(400, guess[1], 0.5);
        Assert.Equal(sign * 800, guess[2], 1.0);
        Assert.Equal(0, guess[3]);
    }

    [Fact]
    public void Fit_recovers_absorption_and_dispersion_mix()
    {
        var field = Field();
        double[] truth = [5200, 350, 900, 120];
        var data = field.Select(h => Shape.Evaluate(h, truth)).ToArray();

        var result = PeakFitter.Fit(field, data, [PeakFitter.GuessPeak(Shape, field, data, [])]);

        var fitted = result.Peaks[0].Parameters;
        for (var k = 0; k < truth.Length; k++)
        {
            var value = k == 1 ? Math.Abs(fitted[k]) : fitted[k]; // the width is used as |w|
            Assert.Equal(truth[k], value, Math.Abs(truth[k]) * 1e-5 + 1e-6);
        }
    }

    [Fact]
    public void Fit_of_the_integral_gives_the_parameters_of_the_derivative_fit()
    {
        // The derivative line integrated over field is the absorption line with the same parameters.
        var field = Field();
        double[] truth = [5200, 350, 900, 120];
        var derivative = field.Select(h => DerivativeShape.Evaluate(h, truth)).ToArray();
        var integral = SignalCalculus.Integrate(field, derivative);
        // The running integral starts at 0 at the first point; the line's own value there is the offset.
        var offset = Shape.Evaluate(field[0], truth);
        var data = integral.Select(v => v + offset).ToArray();

        var result = PeakFitter.Fit(field, data, [PeakFitter.GuessPeak(Shape, field, data, [])]);

        Assert.Equal(5200, result.Peaks[0].Parameters[0], 1.0);
        Assert.Equal(350, Math.Abs(result.Peaks[0].Parameters[1]), 1.0);
        Assert.Equal(900, result.Peaks[0].Parameters[2], 900 * 0.01);
    }

    [Fact]
    public void Keeping_the_height_scales_A1_and_A2_with_the_width()
    {
        var peak = new Peak(Shape, [5000, 200, 400, 100]);

        var wider = peak.WithWidthKeepingHeight(1, 300);

        Assert.Equal([5000, 300, 600, 150], wider.Parameters);
        Assert.Equal(Shape.Evaluate(5000, peak.Parameters), Shape.Evaluate(5000, wider.Parameters), 12);
        Assert.True(PeakScaling.Applies(Shape));
    }

    [Fact]
    public void Amplitudes_are_in_data_units_times_Oe()
    {
        Assert.Equal("mV·Oe", ParameterUnits.Of(ParameterKind.AmplitudeField));
        Assert.Equal("mV*Oe^2", ParameterUnits.AsciiOfData(ParameterKind.AmplitudeField, 1)); // fitted on an integral
        Assert.Equal("mV", ParameterUnits.OfData(ParameterKind.AmplitudeField, -1));
    }
}
