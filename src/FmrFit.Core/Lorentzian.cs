namespace FmrFit.Core;

/// <summary>
/// Symmetric Lorentzian absorption line shape, the basic FMR resonance profile.
/// </summary>
public static class Lorentzian
{
    /// <summary>
    /// Evaluates the Lorentzian at field <paramref name="field"/>.
    /// </summary>
    /// <param name="field">Applied magnetic field H.</param>
    /// <param name="h0">Resonance field H0 (peak position).</param>
    /// <param name="deltaH">Half width at half maximum (HWHM).</param>
    /// <param name="amplitude">Peak height at H = H0.</param>
    public static double Evaluate(double field, double h0, double deltaH, double amplitude)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deltaH);
        var x = (field - h0) / deltaH;
        return amplitude / (1.0 + x * x);
    }
}
