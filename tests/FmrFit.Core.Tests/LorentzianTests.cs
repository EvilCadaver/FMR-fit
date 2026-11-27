using FmrFit.Core;

namespace FmrFit.Core.Tests;

public class LorentzianTests
{
    [Fact]
    public void Peak_equals_amplitude_at_resonance()
    {
        Assert.Equal(2.5, Lorentzian.Evaluate(field: 100, h0: 100, deltaH: 5, amplitude: 2.5), 12);
    }

    [Fact]
    public void Value_is_half_maximum_at_one_hwhm_from_resonance()
    {
        Assert.Equal(1.0, Lorentzian.Evaluate(field: 105, h0: 100, deltaH: 5, amplitude: 2), 12);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7.3)]
    [InlineData(40)]
    public void Is_symmetric_around_resonance(double offset)
    {
        var left = Lorentzian.Evaluate(100 - offset, 100, 5, 1);
        var right = Lorentzian.Evaluate(100 + offset, 100, 5, 1);
        Assert.Equal(left, right, 12);
    }

    [Fact]
    public void Non_positive_width_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Lorentzian.Evaluate(0, 0, 0, 1));
    }
}
