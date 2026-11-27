namespace FmrFit.Core.Numbers;

/// <summary>
/// Mouse-wheel stepping of a number entry. Steps are powers of ten, 10^exponent, and every step lands on a multiple
/// of the step, so repeated steps never accumulate floating-point noise (0.1 three times is 0.3, not 0.30000000000000004).
/// </summary>
public static class WheelStep
{
    /// <summary>Exponents are kept in this range so that 10^exponent stays a finite, non-zero double.</summary>
    private const int MaxExponent = 300;

    /// <summary>
    /// Decimal exponent of a step proportional to <paramref name="value"/>: one unit in its second significant digit
    /// (3.2E-5 → −6, 4500 → 2), so one step changes the value by 1–10 %. Null for 0 or a value that is not a number.
    /// </summary>
    public static int? RelativeExponent(double value)
    {
        if (!double.IsFinite(value) || value == 0)
            return null;
        var magnitude = Math.Abs(value);
        var exponent = (int)Math.Floor(Math.Log10(magnitude));
        // Log10 may be off by one ulp at exact powers of ten.
        if (Math.Pow(10, exponent) > magnitude)
            exponent--;
        else if (Math.Pow(10, exponent + 1) <= magnitude)
            exponent++;
        return Clamp(exponent - 1);
    }

    /// <summary>Exponent of a fixed step given as a power of ten (0.1 → −1, 10 → 1); null if it is not positive.</summary>
    public static int? ExponentOf(double step) =>
        double.IsFinite(step) && step > 0 ? Clamp((int)Math.Round(Math.Log10(step))) : null;

    /// <summary>
    /// <paramref name="value"/> moved by <paramref name="notches"/> steps of 10^<paramref name="exponent"/>: the first
    /// step goes to the next multiple of the step in that direction (3.14 → 3.2 up, 3.1 down), each further step adds
    /// one step. The result is the double nearest to that decimal number.
    /// </summary>
    public static double Apply(double value, int notches, int exponent)
    {
        if (!double.IsFinite(value) || notches == 0)
            return value;
        exponent = Clamp(exponent);
        // Scaling by an exact power of ten and dividing back makes the result the nearest double to the decimal.
        var unit = Math.Pow(10, Math.Abs(exponent));
        var steps = exponent < 0 ? value * unit : value / unit;
        var nearest = Math.Round(steps);
        if (Math.Abs(steps - nearest) < 1e-6)
            steps = nearest;
        var target = notches > 0 ? Math.Floor(steps) + notches : Math.Ceiling(steps) + notches;
        var result = exponent < 0 ? target / unit : target * unit;
        return double.IsFinite(result) ? result : value;
    }

    private static int Clamp(int exponent) => Math.Clamp(exponent, -MaxExponent, MaxExponent);
}
