using FmrFit.Core.Fitting;

namespace FmrFit.Core.Tests.Fitting;

public class LorentzianDerivativeTests
{
    private static readonly LorentzianDerivative Shape = new();

    private static double[] Field() => Enumerable.Range(0, 801).Select(i => 2000 + 10.0 * i).ToArray(); // 2000…10000 Oe

    private static double Absorption(double u, double w) => w / (4 * u * u + w * w);
    private static double Dispersion(double u, double w) => u / (4 * u * u + w * w);

    private static double Derivative(Func<double, double> f, double x) => (f(x + 1e-4) - f(x - 1e-4)) / 2e-4;

    [Fact]
    public void Is_the_default_shape()
    {
        Assert.IsType<LorentzianDerivative>(PeakShapes.All[0]);
    }

    [Theory]
    [InlineData(-1, typeof(LorentzianAbsorption))] // ∫P dH
    [InlineData(0, typeof(LorentzianAbsorption))]  // P: ~P recorded, or dP/dH integrated
    [InlineData(1, typeof(LorentzianDerivative))]  // dP/dH
    [InlineData(2, typeof(LorentzianDerivative))]  // d²P/dH²
    public void Default_shape_follows_the_quantity(int order, Type expected)
    {
        Assert.IsType(expected, PeakShapes.DefaultFor(order));
        Assert.Contains(PeakShapes.DefaultFor(order), PeakShapes.All);
    }

    [Theory]
    [InlineData(4800.0)]
    [InlineData(4950.0)]
    [InlineData(5000.0)]
    [InlineData(5137.0)]
    public void Terms_are_derivatives_of_Lorentzian_absorption_and_dispersion(double h)
    {
        const double xc = 5000, w = 200, a1 = 3.0, a2 = -1.5;

        var expected = a1 / 2 * Derivative(u => Absorption(u, w), h - xc) - a2 * Derivative(u => Dispersion(u, w), h - xc);

        Assert.Equal(expected, Shape.Evaluate(h, [xc, w, a1, a2]), 1e-9);
    }

    [Fact]
    public void Matches_the_given_formula()
    {
        // -A11*4*w1*(x-xc1)/(4*(x-xc1)^2+w1^2)^2 - A12*(w1^2-4*(x-xc1)^2)/(4*(x-xc1)^2+w1^2)^2
        double x = 5123, xc1 = 5000, w1 = 180, a11 = 2.5, a12 = 0.7;
        var d = 4 * Math.Pow(x - xc1, 2) + w1 * w1;
        var formula = -a11 * 4 * w1 * (x - xc1) / (d * d) - a12 * (w1 * w1 - 4 * Math.Pow(x - xc1, 2)) / (d * d);

        Assert.Equal(formula, Shape.Evaluate(x, [xc1, w1, a11, a12]), 15);
    }

    [Fact]
    public void Absorption_extrema_are_at_w_over_2_root3_with_the_expected_height()
    {
        const double xc = 5000, w = 300, a1 = 1e3;
        var offset = w / (2 * Math.Sqrt(3));
        var extremum = LorentzianDerivative.ExtremumFactor * a1 / (w * w);

        // A1 > 0 (FMR): positive lobe on the low-field side.
        Assert.Equal(extremum, Shape.Evaluate(xc - offset, [xc, w, a1, 0]), 12);
        Assert.Equal(-extremum, Shape.Evaluate(xc + offset, [xc, w, a1, 0]), 12);
        Assert.True(Shape.Evaluate(xc - offset * 0.95, [xc, w, a1, 0]) < extremum);
        Assert.True(Shape.Evaluate(xc - offset * 1.05, [xc, w, a1, 0]) < extremum);
    }

    [Fact]
    public void Markers_are_the_centre_and_the_absorption_extrema()
    {
        const double xc = 5000, w = 300, a1 = 1e3;
        double[] p = [xc, -w, a1, 0]; // the width is used as an absolute value

        var markers = Shape.Markers(p)!.Value;

        Assert.Equal(xc, markers.Center);
        Assert.Equal(LorentzianDerivative.ExtremumFactor * a1 / (w * w), Shape.Evaluate(markers.Left, p), 12);
        Assert.Equal(-LorentzianDerivative.ExtremumFactor * a1 / (w * w), Shape.Evaluate(markers.Right, p), 12);
        Assert.Equal(0, Derivative(h => Shape.Evaluate(h, p), markers.Left), 1e-9);
        Assert.Equal(0, Derivative(h => Shape.Evaluate(h, p), markers.Right), 1e-9);
    }

    [Fact]
    public void Zero_width_gives_zero_instead_of_nan()
    {
        Assert.Equal(0, Shape.Evaluate(5000, [5000, 0, 1, 1]));
    }

    [Fact]
    public void Guess_recovers_a_pure_absorption_line()
    {
        var field = Field();
        double[] truth = [5000, 300, 500, 0];
        var signal = field.Select(h => Shape.Evaluate(h, truth)).ToArray();

        var guess = Shape.Guess(field, signal);

        Assert.Equal(5000, guess[0], 10.0);
        Assert.Equal(300, guess[1], 20.0);
        Assert.Equal(500, guess[2], 60.0);
        Assert.Equal(0, guess[3]);
    }

    [Fact]
    public void Fit_recovers_absorption_and_dispersion_mix()
    {
        var field = Field();
        double[] truth = [5000, 250, 600, 200];
        var random = new Random(5);
        var signal = field.Select(h => Shape.Evaluate(h, truth) + 1e-5 * (random.NextDouble() - 0.5)).ToArray();

        var peaks = new List<Peak> { PeakFitter.GuessPeak(Shape, field, signal, []) };
        var result = PeakFitter.Fit(field, signal, peaks);

        var p = result.Peaks[0].Parameters;
        Assert.Equal(5000, p[0], 0.5);
        Assert.Equal(250, Math.Abs(p[1]), 0.5);
        Assert.Equal(600, p[2], 2.0);
        Assert.Equal(200, p[3], 2.0);
        Assert.Equal(1e-5 / Math.Sqrt(12), result.StandardDeviation, 5e-7);
    }
}
