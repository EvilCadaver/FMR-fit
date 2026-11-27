using FmrFit.Core.Fitting;
using FmrFit.Core.IO;

namespace FmrFit.Core.Tests.Fitting;

public class PeakCurvesTests
{
    private static readonly LorentzianDerivative Lorentzian = new();
    private static readonly LinearBackground Background = new();

    private static double[] Field() => Enumerable.Range(0, 801).Select(i => 2000 + 10.0 * i).ToArray(); // 2000…10000 Oe

    /// <summary>Two Lorentzian derivatives on a tilted background, with default limits.</summary>
    private static List<Peak> Peaks(double[] field) =>
    [
        Limited(new Peak(Lorentzian, [4500, 200, 5e5, 1e5]), field),
        Limited(new Peak(Lorentzian, [7000, 400, -2e6, 0]), field),
        Limited(new Peak(Background, [0.3, 1e-5]), field),
    ];

    private static Peak Limited(Peak peak, double[] field) =>
        peak with { Limits = PeakLimits.Default(peak.Shape, peak.Parameters, field.Min(), field.Max()), KeepHeight = true };

    private static double[] Signal(double[] field, IReadOnlyList<Peak> peaks)
    {
        var random = new Random(5);
        return field.Select(h => peaks.Sum(p => p.Evaluate(h)) + 0.01 * (random.NextDouble() - 0.5)).ToArray();
    }

    [Fact]
    public void First_update_evaluates_every_peak_and_sums_like_the_fitter()
    {
        var field = Field();
        var peaks = Peaks(field);
        var signal = Signal(field, peaks);

        var curves = PeakCurves.Empty.Update(field, peaks);

        Assert.Equal(3, curves.Evaluated);
        Assert.Equal(PeakFitter.Evaluate(field, signal, peaks).Fit, curves.Sum);
        for (var i = 0; i < peaks.Count; i++)
            Assert.Equal(peaks[i].Evaluate(field), curves.Curves[i]);
    }

    [Fact]
    public void Changing_one_parameter_evaluates_only_that_peak()
    {
        var field = Field();
        var peaks = Peaks(field);
        var before = PeakCurves.Empty.Update(field, peaks);

        peaks[1] = peaks[1].WithParameter(0, 7100);
        var after = before.Update(field, peaks);

        Assert.Equal(1, after.Evaluated);
        Assert.Same(before.Curves[0], after.Curves[0]);
        Assert.Same(before.Curves[2], after.Curves[2]);
        Assert.NotSame(before.Curves[1], after.Curves[1]);
        // Exactly the same numbers as evaluating everything again.
        var fresh = PeakCurves.Empty.Update(field, peaks);
        Assert.Equal(fresh.Sum, after.Sum);
        Assert.Equal(fresh.Curves[1], after.Curves[1]);
    }

    [Fact]
    public void Width_change_keeping_height_evaluates_only_that_peak()
    {
        var field = Field();
        var peaks = Peaks(field);
        var before = PeakCurves.Empty.Update(field, peaks);

        peaks[0] = peaks[0].WithWidthKeepingHeight(1, 300);
        var after = before.Update(field, peaks);

        Assert.Equal(1, after.Evaluated);
        Assert.Equal(PeakCurves.Empty.Update(field, peaks).Sum, after.Sum);
    }

    [Fact]
    public void Fixing_changing_a_limit_or_removing_a_peak_evaluates_nothing()
    {
        var field = Field();
        var peaks = Peaks(field);
        var curves = PeakCurves.Empty.Update(field, peaks);

        peaks[0] = peaks[0].WithFixed(1, true);
        curves = curves.Update(field, peaks);
        Assert.Equal(0, curves.Evaluated);

        peaks[1] = peaks[1].WithMaximum(0, 9000); // value 7000 stays inside
        curves = curves.Update(field, peaks);
        Assert.Equal(0, curves.Evaluated);

        peaks.RemoveAt(0);
        curves = curves.Update(field, peaks);
        Assert.Equal(0, curves.Evaluated);
        Assert.Equal(PeakCurves.Empty.Update(field, peaks).Sum, curves.Sum);
    }

    [Fact]
    public void Adding_a_peak_evaluates_only_the_new_one()
    {
        var field = Field();
        var peaks = Peaks(field);
        var curves = PeakCurves.Empty.Update(field, peaks);

        peaks.Add(new Peak(Lorentzian, [5500, 150, 1e5, 0]));
        curves = curves.Update(field, peaks);

        Assert.Equal(1, curves.Evaluated);
        Assert.Equal(PeakCurves.Empty.Update(field, peaks).Sum, curves.Sum);
    }

    [Fact]
    public void Identical_peaks_each_get_a_curve()
    {
        var field = Field();
        var peak = new Peak(Lorentzian, [4500, 200, 5e5, 1e5]);
        var curves = PeakCurves.Empty.Update(field, [peak]);

        curves = curves.Update(field, [peak, peak]);

        Assert.Equal(1, curves.Evaluated);
        Assert.Equal(PeakCurves.Empty.Update(field, [peak, peak]).Sum, curves.Sum);
    }

    [Fact]
    public void A_new_field_array_evaluates_every_peak()
    {
        var field = Field();
        var peaks = Peaks(field);
        var curves = PeakCurves.Empty.Update(field, peaks);

        curves = curves.Update((double[])field.Clone(), peaks);

        Assert.Equal(3, curves.Evaluated);
    }

    [Fact]
    public void Compare_gives_the_fitter_statistics_over_the_range()
    {
        var field = Field();
        var peaks = Peaks(field);
        var signal = Signal(field, peaks);
        peaks[0] = peaks[0].WithParameter(0, 4520).WithFixed(3, true); // some misfit, one fixed parameter
        var range = new FieldRange(3000, 8000);

        var result = PeakCurves.Empty.Update(field, peaks).Compare(signal, range);

        var (inField, inSignal) = range.Slice(field, signal);
        AssertSameStatistics(PeakFitter.Evaluate(inField, inSignal, peaks), result);
        Assert.Equal(501, result.Points);
        Assert.Equal(9, result.FreeParameters);
    }

    [Fact]
    public void Residual_is_data_minus_sum_over_the_whole_field()
    {
        var field = Field();
        var peaks = Peaks(field);
        var signal = Signal(field, peaks);
        var curves = PeakCurves.Empty.Update(field, peaks);

        Assert.Equal(PeakFitter.Evaluate(field, signal, peaks).Residual, curves.Residual(signal));
    }

    [Fact]
    public void Real_work_file_statistics_match_the_fitter_after_an_edit()
    {
        var m = FmrCsvReader.Read(Path.Combine(AppContext.BaseDirectory, "data", "MM100610_cprllH_42G.csv"));
        var field = m.Field.Where((_, i) => i % 3 == 0).ToArray();
        var signal = m.X.Where((_, i) => i % 3 == 0).Select(v => v * 1000).ToArray();
        var peaks = new List<Peak> { PeakFitter.GuessPeak(Lorentzian, field, signal, []) };
        peaks.Add(PeakFitter.GuessPeak(Background, field, signal, peaks));
        var range = FieldRange.Of(field)!.Value;
        var curves = PeakCurves.Empty.Update(field, peaks);

        peaks[0] = peaks[0].WithWidthKeepingHeight(1, Math.Abs(peaks[0].Parameters[1]) * 1.2);
        curves = curves.Update(field, peaks);

        Assert.Equal(1, curves.Evaluated);
        AssertSameStatistics(PeakFitter.Evaluate(field, signal, peaks), curves.Compare(signal, range));
    }

    private static void AssertSameStatistics(FitResult expected, FitResult actual)
    {
        Assert.Equal(expected.Points, actual.Points);
        Assert.Equal(expected.FreeParameters, actual.FreeParameters);
        Assert.Equal(expected.Fit, actual.Fit);
        Assert.Equal(expected.Residual, actual.Residual);
        Assert.Equal(expected.ChiSquared, actual.ChiSquared);
        Assert.Equal(expected.ReducedChiSquared, actual.ReducedChiSquared);
        Assert.Equal(expected.RSquared, actual.RSquared);
    }
}
