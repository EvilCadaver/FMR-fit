using FmrFit.Core.Processing;

namespace FmrFit.Core.Tests.Processing;

public class PowerNormalizationTests
{
    [Theory]
    [InlineData(10, 1)]
    [InlineData(20, 0.1)]
    [InlineData(0, 10)]
    [InlineData(15, 0.31622776601683794)]
    public void Factor_scales_linearly_in_power_to_10_dBm(double powerDbm, double expected)
    {
        Assert.Equal(expected, PowerNormalization.Factor(powerDbm), 12);
    }
}
