using FmrFit.Core.Fitting;

namespace FmrFit.Core.Tests.Fitting;

public class PeakScalingTests
{
    private static readonly LorentzianDerivative Lorentzian = new();
    private static readonly AsymmetricGaussianDerivative Gaussian = new();

    private static ParameterRange[] LorentzianLimits(double w, double a) =>
        [new(0, 20000), new(0, w), new(-a, a), new(-a, a)];

    [Fact]
    public void Applies_to_shapes_with_amplitudes_in_field_squared_units_only()
    {
        Assert.True(PeakScaling.Applies(Lorentzian));
        Assert.False(PeakScaling.Applies(Gaussian));
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(0.5)]
    [InlineData(1.37)]
    public void Lorentzian_keeps_its_shape_and_height_stretched_along_the_field(double s)
    {
        const double xc = 5000, w = 200;
        var peak = new Peak(Lorentzian, [xc, w, 3e4, -1.2e4], Limits: LorentzianLimits(1e4, 1e7));

        var scaled = peak.WithWidthKeepingHeight(1, s * w);

        Assert.Equal(s * w, scaled.Parameters[1], 9);
        Assert.Equal(3e4 * s * s, scaled.Parameters[2], 6);
        Assert.Equal(-1.2e4 * s * s, scaled.Parameters[3], 6);
        foreach (var u in new[] { -400.0, -57.7, 0, 33, 250 })
            Assert.Equal(peak.Evaluate(xc + u), scaled.Evaluate(xc + s * u), 12);
    }

    [Fact]
    public void Lorentzian_extremum_height_is_unchanged()
    {
        var peak = new Peak(Lorentzian, [5000, 200, 3e4, 0], Limits: LorentzianLimits(1e4, 1e7));

        var scaled = peak.WithWidthKeepingHeight(1, 700);

        // Pure absorption: extremum ExtremumFactor·A1/w² at the low-field marker.
        Assert.Equal(LorentzianDerivative.ExtremumFactor * 3e4 / (200.0 * 200), scaled.Evaluate(scaled.Shape.Markers(scaled.Parameters)!.Value.Left), 12);
    }

    [Fact]
    public void Amplitude_limits_grow_when_needed_and_never_shrink()
    {
        var peak = new Peak(Lorentzian, [5000, 200, 3e4, -1e4], Limits: LorentzianLimits(1e4, 5e4));

        var wider = peak.WithWidthKeepingHeight(1, 400);      // A1 → 1.2e5 > 5e4, A2 → −4e4 within
        var narrower = peak.WithWidthKeepingHeight(1, 100);   // amplitudes shrink; limits stay

        Assert.Equal(1.2e5, wider.Parameters[2], 6);
        Assert.Equal(new ParameterRange(-5e4, 1.2e5), wider.Limits![2]); // only the crossed end moves
        Assert.Equal(new ParameterRange(-5e4, 5e4), wider.Limits[3]);
        Assert.Equal(new ParameterRange(-5e4, 5e4), narrower.Limits![2]);
        Assert.Equal(7500, narrower.Parameters[2], 6);
    }

    [Fact]
    public void A_positive_minimum_is_lowered_when_a_narrower_line_needs_it()
    {
        ParameterRange[] limits = [new(0, 20000), new(0, 1e4), new(2e4, 2e5), new(-1e5, 1e5)];
        var peak = new Peak(Lorentzian, [5000, 200, 3e4, 0], Limits: limits);

        var narrower = peak.WithWidthKeepingHeight(1, 100); // A1 → 7500 < minimum 2e4

        Assert.Equal(7500, narrower.Parameters[2], 6);
        Assert.Equal(new ParameterRange(7500, 2e5), narrower.Limits![2]);
    }

    [Fact]
    public void Width_is_clamped_to_its_limit_before_scaling()
    {
        var peak = new Peak(Lorentzian, [5000, 200, 3e4, 0], Limits: LorentzianLimits(300, 1e7));

        var scaled = peak.WithWidthKeepingHeight(1, 600);

        Assert.Equal(300, scaled.Parameters[1]);
        Assert.Equal(3e4 * 1.5 * 1.5, scaled.Parameters[2], 6);
    }

    [Fact]
    public void Zero_width_before_leaves_the_amplitudes()
    {
        // A zero width can only come from a peak built directly; manual changes stop at the minimum width.
        var peak = new Peak(Lorentzian, [5000, 0, 3e4, 0], Limits: LorentzianLimits(1e4, 1e7));

        var fromZero = peak.WithWidthKeepingHeight(1, 100);

        Assert.Equal(100, fromZero.Parameters[1]);
        Assert.Equal(3e4, fromZero.Parameters[2]);
    }

    [Fact]
    public void Width_below_the_minimum_scales_to_the_minimum_width()
    {
        var peak = new Peak(Lorentzian, [5000, 200, 3e4, 0], Limits: LorentzianLimits(1e4, 1e7));

        var narrowest = peak.WithWidthKeepingHeight(1, 0);

        Assert.Equal(PeakLimits.MinWidth, narrowest.Parameters[1]);
        Assert.Equal(3e4 * Math.Pow(PeakLimits.MinWidth / 200, 2), narrowest.Parameters[2], 12);
    }

    [Fact]
    public void Gaussian_amplitude_already_is_the_height_and_is_left_unchanged()
    {
        var peak = new Peak(Gaussian, [5000, 0.02, 100, 300]);

        var scaled = peak.WithWidthKeepingHeight(2, 250);

        Assert.Equal([5000, 0.02, 250, 300], scaled.Parameters);
    }

    [Fact]
    public void Only_widths_can_be_scaled()
    {
        var peak = new Peak(Lorentzian, [5000, 200, 3e4, 0]);

        Assert.Throws<ArgumentException>(() => peak.WithWidthKeepingHeight(2, 1));
    }

    [Fact]
    public void New_peaks_keep_their_height_by_default()
    {
        var field = Enumerable.Range(0, 801).Select(i => 2000 + 10.0 * i).ToArray();
        var signal = field.Select(h => Lorentzian.Evaluate(h, [5000, 200, 3e4, 0])).ToArray();

        Assert.True(PeakFitter.GuessPeak(Lorentzian, field, signal, []).KeepHeight);
        Assert.False(new Peak(Lorentzian, [5000, 200, 3e4, 0]).KeepHeight);
    }
}
