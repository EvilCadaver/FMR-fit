using FmrFit.Core.Fitting;
using FmrFit.Core.IO;

namespace FmrFit.Core.Tests.Fitting;

public class FieldRangeTests
{
    private static readonly LorentzianDerivative Lorentzian = new();

    private static double[] Field() => Enumerable.Range(0, 1001).Select(i => 100 + 10.0 * i).ToArray(); // 100…10100 Oe

    [Fact]
    public void Of_is_the_full_data_range()
    {
        Assert.Equal(new FieldRange(100, 10100), FieldRange.Of(Field()));
        Assert.Equal(new FieldRange(-5, 7), FieldRange.Of([3, -5, double.NaN, 7]));
        Assert.Null(FieldRange.Of([]));
        Assert.Null(FieldRange.Of([4, 4]));
    }

    [Fact]
    public void Create_clamps_to_the_bounds_and_needs_min_below_max()
    {
        var bounds = new FieldRange(140, 11230);

        Assert.Equal(new FieldRange(4500, 8000), FieldRange.Create(4500, 8000, bounds));
        Assert.Equal(new FieldRange(140, 8000), FieldRange.Create(-500, 8000, bounds));
        Assert.Equal(new FieldRange(4500, 11230), FieldRange.Create(4500, 1e6, bounds));
        Assert.Null(FieldRange.Create(9000, 8000, bounds));
        Assert.Null(FieldRange.Create(8000, 8000, bounds));
        Assert.Null(FieldRange.Create(20000, 30000, bounds)); // both clamp to 11230
        Assert.Null(FieldRange.Create(double.NaN, 8000, bounds));
    }

    [Fact]
    public void Slice_keeps_exactly_the_points_inside_ends_included()
    {
        double[] field = [100, 200, 300, 400, 500];
        double[] signal = [1, 2, 3, 4, 5];

        var (f, s) = new FieldRange(200, 400).Slice(field, signal);

        Assert.Equal([200.0, 300, 400], f);
        Assert.Equal([2.0, 3, 4], s);
        Assert.Throws<ArgumentException>(() => new FieldRange(0, 1).Slice(field, [1]));
    }

    [Fact]
    public void View_is_one_point_one_times_the_range_centred_on_it()
    {
        var view = new FieldRange(4500, 8000).View();

        Assert.Equal(4325, view.Min, 9);
        Assert.Equal(8175, view.Max, 9);
        Assert.Equal(1.1 * 3500, view.Width, 9);
    }

    [Fact]
    public void Extent_covers_only_points_inside_and_skips_non_finite_values()
    {
        double[] field = [100, 200, 300, 400];
        double[] a = [-50, 1, double.NaN, 3];
        double[] b = [99, -2, 5, double.PositiveInfinity];

        Assert.Equal((-2.0, 5.0), new FieldRange(150, 450).Extent(field, a, b));
        Assert.Equal((1.0, 1.0), new FieldRange(150, 250).Extent(field, a));
        Assert.Null(new FieldRange(500, 600).Extent(field, a, b));
    }

    [Fact]
    public void Fitting_only_the_resonance_range_ignores_a_dip_and_baseline_step_elsewhere()
    {
        // FMR line at 5700 Oe plus an FMAR-like dip at 1500 Oe and a raised baseline after it, all outside the range.
        var field = Field();
        double[] truth = [5700, 600, 1e6, -2e5];
        var signal = field.Select(h =>
            Lorentzian.Evaluate(h, truth)
            - 0.4 * 250 * 250 / (4 * (h - 1500) * (h - 1500) + 250 * 250)
            + (h > 1500 && h < 3800 ? 0.3 : 0)).ToArray();
        var range = new FieldRange(4000, 8000);
        var (f, s) = range.Slice(field, signal);

        var inRange = PeakFitter.Fit(f, s, [PeakFitter.GuessPeak(Lorentzian, f, s, [])]);
        var whole = PeakFitter.Fit(field, signal, [PeakFitter.GuessPeak(Lorentzian, field, signal, [])]);

        var p = inRange.Peaks[0].Parameters;
        Assert.Equal(5700, p[0], 15.0);
        Assert.Equal(600, p[1], 30.0);
        Assert.True(inRange.RSquared > 0.98, $"R² in range {inRange.RSquared}");
        Assert.True(Math.Abs(whole.Peaks[0].Parameters[0] - 5700) > Math.Abs(p[0] - 5700)
                    || whole.RSquared < inRange.RSquared, "the full-range fit should be worse");
        Assert.Equal(f.Length, inRange.Points);
    }

    [Fact]
    public void Real_resonance_fits_better_within_its_range()
    {
        var m = FmrCsvReader.Read(Path.Combine(AppContext.BaseDirectory, "data", "MM100610_cprllH_42G.csv"));
        var field = m.Field.Where((_, i) => i % 3 == 0).ToArray();
        var signal = m.X.Where((_, i) => i % 3 == 0).Select(v => v * 1000).ToArray();
        var whole = PeakFitter.Fit(field, signal, [PeakFitter.GuessPeak(Lorentzian, field, signal, [])]);
        var xc = whole.Peaks[0].Parameters[0];
        var w = whole.Peaks[0].Parameters[1];

        var range = FieldRange.Create(xc - 3 * w, xc + 3 * w, FieldRange.Of(field)!.Value)!.Value;
        var (f, s) = range.Slice(field, signal);
        var local = PeakFitter.Fit(f, s, [PeakFitter.GuessPeak(Lorentzian, f, s, [])]);

        Assert.InRange(local.Peaks[0].Parameters[0], range.Min, range.Max);
        Assert.True(local.RSquared >= whole.RSquared, $"range R² {local.RSquared} vs whole {whole.RSquared}");
    }
}
