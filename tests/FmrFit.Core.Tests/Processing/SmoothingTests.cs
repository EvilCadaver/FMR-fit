using FmrFit.Core.IO;
using FmrFit.Core.Processing;

namespace FmrFit.Core.Tests.Processing;

public class SmoothingTests
{
    private static double[] Values(int n, Func<int, double> f) => Enumerable.Range(0, n).Select(f).ToArray();

    [Fact]
    public void Fft_removes_components_shorter_than_the_span_and_keeps_slower_ones()
    {
        // n = 101 points: cos(π·5·i/100) has a period of 40 points, (−1)^i a period of 2 points.
        double Slow(int i) => 2 + Math.Cos(Math.PI * 5 * i / 100);
        var values = Values(101, i => Slow(i) + 0.5 * (i % 2 == 0 ? 1 : -1));

        var smoothed = Smoothing.FftLowPass(values, 3);

        for (var i = 0; i < values.Length; i++)
            Assert.Equal(Slow(i), smoothed[i], 9);
    }

    [Fact]
    public void Fft_with_a_span_of_two_points_keeps_the_data()
    {
        var values = Values(50, i => Math.Sin(i * 0.7) + (i % 3));

        var smoothed = Smoothing.FftLowPass(values, 2);

        for (var i = 0; i < values.Length; i++)
            Assert.Equal(values[i], smoothed[i], 9);
    }

    [Fact]
    public void Fft_cuts_a_period_just_below_the_span()
    {
        // Period 10 points is kept with a span of 10, removed with a span of 11.
        var values = Values(101, i => Math.Cos(2 * Math.PI * i / 10));

        Assert.Equal(values[0], Smoothing.FftLowPass(values, 10)[0], 9);
        Assert.All(Smoothing.FftLowPass(values, 11), v => Assert.Equal(0, v, 9));
    }

    [Fact]
    public void Savitzky_golay_five_point_quadratic_has_the_textbook_weights()
    {
        var impulse = new double[21];
        impulse[10] = 35;

        var smoothed = Smoothing.SavitzkyGolay(impulse, 5, 2);

        Assert.Equal([-3.0, 12, 17, 12, -3], smoothed[8..13].Select(v => Math.Round(v, 9)));
        Assert.All(smoothed[..8], v => Assert.Equal(0, v, 9));
    }

    [Theory]
    [InlineData(5, 2)]
    [InlineData(7, 3)]
    [InlineData(9, 4)]
    public void Savitzky_golay_keeps_a_polynomial_of_its_order_also_at_the_ends(int points, int order)
    {
        double P(int i) => Enumerable.Range(0, order + 1).Sum(k => Math.Pow(0.1 * i - 2, k) * (k + 1));
        var values = Values(40, P);

        var smoothed = Smoothing.SavitzkyGolay(values, points, order);

        for (var i = 0; i < values.Length; i++)
            Assert.Equal(values[i], smoothed[i], 8);
    }

    [Fact]
    public void Savitzky_golay_reduces_noise()
    {
        var random = new Random(1);
        var noise = Values(500, _ => random.NextDouble() - 0.5);

        var smoothed = Smoothing.SavitzkyGolay(noise, 11, 2);

        static double Rms(double[] v) => Math.Sqrt(v.Average(x => x * x));
        Assert.True(Rms(smoothed) < 0.6 * Rms(noise));
    }

    [Fact]
    public void Too_short_data_are_returned_unchanged()
    {
        double[] values = [1, 5, 2];

        Assert.Equal(values, Smoothing.SavitzkyGolay(values, 5, 2));
        Assert.Equal([7.0, 8], Smoothing.FftLowPass([7.0, 8], 3));
    }

    [Fact]
    public void None_returns_a_copy()
    {
        double[] values = [1, 5, 2];

        var result = Smoothing.Apply(values, SmoothingSettings.None);

        Assert.Equal(values, result);
        Assert.NotSame(values, result);
    }

    [Fact]
    public void Invalid_savitzky_golay_windows_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Smoothing.SavitzkyGolay(new double[10], 4, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => Smoothing.SavitzkyGolay(new double[10], 5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Smoothing.FftLowPass(new double[10], 1));
    }

    [Theory]
    [InlineData(SmoothingMethod.SavitzkyGolay, 4, 2, 5, 2)]  // even → odd
    [InlineData(SmoothingMethod.SavitzkyGolay, 3, 4, 5, 4)]  // window above the order
    [InlineData(SmoothingMethod.SavitzkyGolay, 3, 1, 3, 1)]
    [InlineData(SmoothingMethod.SavitzkyGolay, 5000, 2, 999, 2)]
    [InlineData(SmoothingMethod.Fft, 1, 2, 2, 2)]
    [InlineData(SmoothingMethod.Fft, 4, 2, 4, 2)]            // FFT spans may be even
    public void Coerce_makes_settings_valid(SmoothingMethod method, int points, int order, int expectedPoints, int expectedOrder)
    {
        var coerced = new SmoothingSettings(method, points, order).Coerce();

        Assert.Equal(new SmoothingSettings(method, expectedPoints, expectedOrder), coerced);
    }

    [Fact]
    public void Defaults_are_fft_3_and_savitzky_golay_5_quadratic()
    {
        Assert.Equal(new SmoothingSettings(SmoothingMethod.Fft, 3, 2), SmoothingSettings.Default(SmoothingMethod.Fft));
        Assert.Equal(new SmoothingSettings(SmoothingMethod.SavitzkyGolay, 5, 2), SmoothingSettings.Default(SmoothingMethod.SavitzkyGolay));
        Assert.Equal(SmoothingSettings.None, SmoothingSettings.Default(SmoothingMethod.None));
        Assert.Equal(SmoothingSettings.None, new SmoothingSettings(SmoothingMethod.None, 7, 3).Coerce());
    }

    [Theory]
    [InlineData(SmoothingMethod.None, "off")]
    [InlineData(SmoothingMethod.Fft, "fft")]
    [InlineData(SmoothingMethod.SavitzkyGolay, "savitzky-golay")]
    public void Method_text_round_trips(SmoothingMethod method, string text)
    {
        Assert.Equal(text, SmoothingMethodText.ToText(method));
        Assert.Equal(method, SmoothingMethodText.Parse(text));
    }

    [Fact]
    public void Settings_are_read_back_from_work_file_processing()
    {
        var p = new WorkFileProcessing(0, 0, 0, 0, 0, "", 0, 0, 0, 0, 0, 0, 10, null, null);

        Assert.Equal(SmoothingSettings.None, SmoothingSettings.FromWorkFile(p));
        Assert.Equal(new SmoothingSettings(SmoothingMethod.SavitzkyGolay, 9, 3),
            SmoothingSettings.FromWorkFile(p with { Smoothing = "savitzky-golay", SmoothingPoints = 9, SmoothingPolynomialOrder = 3 }));
        Assert.Equal(SmoothingSettings.Default(SmoothingMethod.Fft),
            SmoothingSettings.FromWorkFile(p with { Smoothing = "fft" }));
    }
}
