using System.Globalization;
using FmrFit.Core.Numbers;

namespace FmrFit.Core.Tests.Numbers;

public class WheelStepTests
{
    [Theory]
    [InlineData(3.2e-5, -6)]
    [InlineData(-3.2e-5, -6)]
    [InlineData(1e-5, -6)]
    [InlineData(9.99e-6, -7)]
    [InlineData(4500, 2)]
    [InlineData(1000, 2)]
    [InlineData(999.9, 1)]
    [InlineData(0.05, -3)]
    [InlineData(1, -1)]
    public void Relative_step_is_one_unit_of_the_second_significant_digit(double value, int exponent) =>
        Assert.Equal(exponent, WheelStep.RelativeExponent(value));

    [Fact]
    public void Zero_and_non_numbers_have_no_relative_step()
    {
        Assert.Null(WheelStep.RelativeExponent(0));
        Assert.Null(WheelStep.RelativeExponent(double.NaN));
        Assert.Null(WheelStep.RelativeExponent(double.PositiveInfinity));
    }

    [Fact]
    public void Fixed_steps_are_powers_of_ten()
    {
        Assert.Equal(-1, WheelStep.ExponentOf(0.1));
        Assert.Equal(0, WheelStep.ExponentOf(1));
        Assert.Equal(1, WheelStep.ExponentOf(10));
        Assert.Null(WheelStep.ExponentOf(0));
        Assert.Null(WheelStep.ExponentOf(-1));
    }

    [Fact]
    public void First_step_goes_to_the_next_multiple_in_that_direction()
    {
        Assert.Equal(3.2, WheelStep.Apply(3.14, 1, -1));
        Assert.Equal(3.1, WheelStep.Apply(3.14, -1, -1));
        Assert.Equal(3.3e-5, WheelStep.Apply(3.2e-5, 1, -6));
        Assert.Equal(3.1e-5, WheelStep.Apply(3.2e-5, -1, -6));
        Assert.Equal(4600, WheelStep.Apply(4500, 1, 2));
        Assert.Equal(4500, WheelStep.Apply(4512.3, -1, 2));
        Assert.Equal(4400, WheelStep.Apply(4512.3, -2, 2));
        Assert.Equal(3.4, WheelStep.Apply(3.14, 3, -1));
    }

    [Fact]
    public void Steps_cross_zero_with_the_same_size()
    {
        Assert.Equal(0, WheelStep.Apply(1e-6, -1, -6));
        Assert.Equal(-1e-6, WheelStep.Apply(0, -1, -6));
        Assert.Equal(-1e-6, WheelStep.Apply(1e-6, -2, -6));
        Assert.Equal(-0.1, WheelStep.Apply(0, -1, -1));
    }

    [Fact]
    public void Repeated_steps_show_no_floating_point_noise()
    {
        var value = 0.0;
        for (var i = 0; i < 3; i++)
            value = WheelStep.Apply(value, 1, -1);
        Assert.Equal("0.3", value.ToString("R", CultureInfo.InvariantCulture));

        value = 3.2e-5;
        for (var i = 0; i < 7; i++)
            value = WheelStep.Apply(value, 1, -6);
        Assert.Equal("3.9E-05", value.ToString("R", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_value_already_on_the_grid_is_not_held_by_rounding_noise()
    {
        // 0.3 is not exactly representable; 0.3 × 10 is 3.0000000000000004, which must still count as on the grid.
        Assert.Equal(0.2, WheelStep.Apply(0.3, -1, -1));
        Assert.Equal(0.4, WheelStep.Apply(0.3, 1, -1));
    }

    [Fact]
    public void No_notches_or_non_numbers_leave_the_value()
    {
        Assert.Equal(3.14, WheelStep.Apply(3.14, 0, -1));
        Assert.True(double.IsNaN(WheelStep.Apply(double.NaN, 1, -1)));
    }
}
