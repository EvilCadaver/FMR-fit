using FmrFit.Core.Fitting;

namespace FmrFit.Core.Tests.Fitting;

public class AsymmetricGaussianTests
{
    private static readonly AsymmetricGaussian Shape = new();

    private static double[] Field() => Enumerable.Range(0, 801).Select(i => 2000 + 10.0 * i).ToArray();

    [Fact]
    public void Height_is_A_at_H0_and_each_side_has_its_own_width()
    {
        double[] p = [5000, 2.0, 100, 300];

        Assert.Equal(2.0, Shape.Evaluate(5000, p), 12);
        Assert.Equal(2.0 * Math.Exp(-0.5), Shape.Evaluate(4900, p), 12); // H0 − σL
        Assert.Equal(2.0 * Math.Exp(-0.5), Shape.Evaluate(5300, p), 12); // H0 + σR
        Assert.Equal(2.0 * Math.Exp(-2), Shape.Evaluate(4800, p), 12);
    }

    [Fact]
    public void Matches_the_given_formula_and_markers()
    {
        double h = 5123, h0 = 5000, a = 1.7, sr = 250;
        Assert.Equal(a * Math.Exp(-0.5 * Math.Pow((h - h0) / sr, 2)), Shape.Evaluate(h, [h0, a, 90, sr]), 15);
        Assert.Equal(new PeakMarkers(4910, 5000, 5250), Shape.Markers([h0, a, -90, sr]));
        Assert.Equal(0, Shape.Evaluate(4000, [h0, a, 0, sr]));
    }

    [Fact]
    public void Guess_finds_centre_height_and_both_widths()
    {
        var field = Field();
        var data = field.Select(h => Shape.Evaluate(h, [6000, -0.5, 150, 400])).ToArray();

        var guess = Shape.Guess(field, data);

        Assert.Equal(6000, guess[0], 6);
        Assert.Equal(-0.5, guess[1], 9);
        Assert.Equal(150, guess[2], 3.0);
        Assert.Equal(400, guess[3], 3.0);
    }

    [Fact]
    public void Fit_recovers_an_asymmetric_line_from_its_guess()
    {
        var field = Field();
        double[] truth = [5500, 1.2, 200, 450];
        var data = field.Select(h => Shape.Evaluate(h, truth)).ToArray();

        var p = PeakFitter.Fit(field, data, [PeakFitter.GuessPeak(Shape, field, data, [])]).Peaks[0].Parameters;

        Assert.Equal(5500, p[0], 0.01);
        Assert.Equal(1.2, p[1], 1e-6);
        Assert.Equal(200, Math.Abs(p[2]), 0.01);
        Assert.Equal(450, Math.Abs(p[3]), 0.01);
    }

    [Fact]
    public void Width_changes_leave_the_height_alone()
    {
        Assert.False(PeakScaling.Applies(Shape));
    }
}
