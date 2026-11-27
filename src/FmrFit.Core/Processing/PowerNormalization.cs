namespace FmrFit.Core.Processing;

/// <summary>
/// Scales the signal to a reference microwave power, assuming the signal is proportional to the power
/// (square-law detection): factor = 10^((reference − P) / 10) with powers in dBm.
/// Applied as the last step of <see cref="XSignalProcessing.Process"/> via <see cref="XProcessingSettings.Scale"/>.
/// </summary>
public static class PowerNormalization
{
    public const double ReferenceDbm = 10;

    public static double Factor(double powerDbm, double referenceDbm = ReferenceDbm) =>
        Math.Pow(10, (referenceDbm - powerDbm) / 10);
}
