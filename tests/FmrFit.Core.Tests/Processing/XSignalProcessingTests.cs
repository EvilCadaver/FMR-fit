using FmrFit.Core.IO;
using FmrFit.Core.Processing;

namespace FmrFit.Core.Tests.Processing;

public class XSignalProcessingTests
{
    /// <summary>
    /// Up sweep 100→1000 Oe (t = 0…90 s), down 999→200 Oe (t = 90.1…170 s), 1 Oe per 0.1 s;
    /// X and Y given as functions of (t, H).
    /// </summary>
    private static FieldSweeps Sweeps(Func<double, double, double> x, Func<double, double, double>? y = null)
    {
        y ??= (_, _) => 0;
        SweepBranch Branch(double t0, double h0, double h1)
        {
            var n = (int)Math.Round(Math.Abs(h1 - h0)) + 1;
            var time = new double[n];
            var field = new double[n];
            var xs = new double[n];
            var ys = new double[n];
            for (var i = 0; i < n; i++)
            {
                time[i] = t0 + i * 0.1;
                field[i] = h0 + Math.Sign(h1 - h0) * i;
                xs[i] = x(time[i], field[i]);
                ys[i] = y(time[i], field[i]);
            }
            return new SweepBranch(time, field, xs, ys);
        }

        var up = Branch(0, 100, 1000);
        var down = Branch(up.Time[^1] + 0.1, 999, 200);
        return new FieldSweeps(up, down, 10, -10);
    }

    private static XProcessingSettings Settings(
        SweepSelection sweeps = SweepSelection.UpAndDown,
        LinearBackground? time = null,
        LinearBackground? field = null,
        FieldGrid? grid = null) =>
        new(sweeps, time ?? LinearBackground.None, field ?? LinearBackground.None, grid ?? new FieldGrid(200, 990, 10));

    [Theory]
    [InlineData(SweepSelection.UpAndDown, "up&down")]
    [InlineData(SweepSelection.Up, "up")]
    [InlineData(SweepSelection.Down, "down")]
    public void Sweep_selection_text_round_trips(SweepSelection selection, string text)
    {
        Assert.Equal(text, SweepSelectionText.ToText(selection));
        Assert.Equal(selection, SweepSelectionText.Parse(text));
    }

    [Fact]
    public void Linear_background_fit_is_exact_for_a_line()
    {
        var bg = LinearBackground.Fit([1.0, 2, 3, 4], [5.0, 7, 9, 11]);

        Assert.Equal(3, bg.Intercept, 12);
        Assert.Equal(2, bg.Slope, 12);
        Assert.Equal(LinearBackground.None, LinearBackground.Fit([1.0], [2.0]));
    }

    [Fact]
    public void Grid_points_include_both_ends_and_reject_invalid_ranges()
    {
        Assert.Equal([200.0, 210, 220], new FieldGrid(200, 220, 10).Points());
        Assert.Equal([200.0, 210], new FieldGrid(200, 219, 10).Points());
        Assert.Empty(new FieldGrid(300, 200, 10).Points());
        Assert.Empty(new FieldGrid(200, 300, 0).Points());
    }

    [Fact]
    public void Guessed_grid_is_the_full_range_of_selected_sweeps_rounded_inward()
    {
        var s = Sweeps((_, h) => h);

        // Up 100…1000, down 200…999 → full range 100…1000.
        Assert.Equal(new FieldGrid(100, 1000, 10), XSignalProcessing.GuessGrid(s, 0, SweepSelection.UpAndDown));
        Assert.Equal(new FieldGrid(100, 1000, 10), XSignalProcessing.GuessGrid(s, 0, SweepSelection.Up));
        Assert.Equal(new FieldGrid(200, 990, 10), XSignalProcessing.GuessGrid(s, 0, SweepSelection.Down));
        // Field correction 3 Oe: up 97…997, down 203…1002 → full range 97…1002 → 100…1000.
        Assert.Equal(new FieldGrid(100, 1000, 10), XSignalProcessing.GuessGrid(s, 3, SweepSelection.UpAndDown));
    }

    [Fact]
    public void Up_and_down_uses_the_single_sweep_where_only_one_has_data()
    {
        // X = 1 on the up sweep, 3 on the down sweep; up alone covers 100…199 Oe.
        var s = Sweeps((t, _) => t < 90.05 ? 1 : 3);

        var (field, x) = XSignalProcessing.Process(s, 0, 0, Settings(grid: XSignalProcessing.GuessGrid(s, 0, SweepSelection.UpAndDown)));

        Assert.Equal(100, field[0]);
        Assert.Equal(1, x[0], 12);                        // 100 Oe: up only
        Assert.Equal(2, x[Array.IndexOf(field, 500.0)], 12); // 500 Oe: average of both
        Assert.Equal(1, x[^1], 12);                       // 1000 Oe: up only
    }

    [Fact]
    public void Selection_picks_sweeps_and_averages_both_where_they_overlap()
    {
        // X = 1 on the up sweep (t ≤ 90 s), 3 on the down sweep.
        var s = Sweeps((t, _) => t < 90.05 ? 1 : 3);

        Assert.All(XSignalProcessing.Process(s, 0, 0, Settings(SweepSelection.Up)).X, v => Assert.Equal(1, v, 12));
        Assert.All(XSignalProcessing.Process(s, 0, 0, Settings(SweepSelection.Down)).X, v => Assert.Equal(3, v, 12));
        Assert.All(XSignalProcessing.Process(s, 0, 0, Settings()).X, v => Assert.Equal(2, v, 12));
    }

    [Fact]
    public void Grid_points_outside_the_data_are_left_out()
    {
        var s = Sweeps((_, h) => h);

        var (field, _) = XSignalProcessing.Process(s, 0, 0,
            Settings(SweepSelection.Down, grid: new FieldGrid(0, 2000, 100)));

        Assert.Equal([200.0, 300, 400, 500, 600, 700, 800, 900], field);
    }

    [Fact]
    public void Interpolation_is_linear_between_points()
    {
        var s = Sweeps((_, h) => 2 * h + 1);

        var (field, x) = XSignalProcessing.Process(s, 0, 0, Settings(grid: new FieldGrid(200.5, 300.5, 25)));

        for (var i = 0; i < field.Length; i++)
            Assert.Equal(2 * field[i] + 1, x[i], 9);
    }

    [Fact]
    public void Time_and_field_backgrounds_are_subtracted()
    {
        // X = time drift + field background + 5
        var s = Sweeps((t, h) => 0.5 + 0.01 * t + 2 + 3e-3 * h + 5);

        var (_, x) = XSignalProcessing.Process(s, 0, 0, Settings(
            time: new LinearBackground(0.5, 0.01),
            field: new LinearBackground(2, 3e-3)));

        Assert.All(x, v => Assert.Equal(5, v, 9));
    }

    [Fact]
    public void Scale_is_applied_last_with_backgrounds_in_unscaled_units()
    {
        var s = Sweeps((_, h) => 2 + 3e-3 * h + 5);

        var (_, x) = XSignalProcessing.Process(s, 0, 0, Settings(field: new LinearBackground(2, 3e-3)) with { Scale = 0.5 });

        Assert.All(x, v => Assert.Equal(2.5, v, 9));
    }

    [Fact]
    public void Smoothing_is_applied_to_the_interpolated_values_before_scaling()
    {
        // Wiggle in field, so smoothing changes it.
        var s = Sweeps((_, h) => Math.Sin(h / 15.0));
        var plain = Settings(grid: new FieldGrid(200, 990, 10)) with { Scale = 0.5 };
        var smoothing = new SmoothingSettings(SmoothingMethod.SavitzkyGolay, 7, 2);

        var (field, x) = XSignalProcessing.Process(s, 0, 0, plain with { Smoothing = smoothing });

        var (plainField, plainX) = XSignalProcessing.Process(s, 0, 0, plain);
        var expected = Smoothing.Apply(plainX, smoothing);
        Assert.Equal(plainField, field);
        for (var i = 0; i < x.Length; i++)
            Assert.Equal(expected[i], x[i], 12);
        Assert.NotEqual(plainX[10], x[10], 6);
    }

    [Fact]
    public void Auto_field_background_fits_after_time_background_inside_the_grid_range()
    {
        // Field background only inside 400…800 Oe; outside it a different slope that must be ignored.
        var s = Sweeps((t, h) => 0.02 * t + (h is >= 400 and <= 800 ? 1 + 2e-3 * h : 50));

        var bg = XSignalProcessing.AutoFieldBackground(s, 0, 0, Settings(
            time: new LinearBackground(0, 0.02), grid: new FieldGrid(400, 800, 10)));

        Assert.Equal(1, bg.Intercept, 9);
        Assert.Equal(2e-3, bg.Slope, 12);
    }

    [Fact]
    public void Uses_phase_rotated_X_and_corrected_field()
    {
        // Signal entirely in Y; a +90° phase change moves it into X: X' = X cos 90 + Y sin 90 = Y.
        var s = Sweeps((_, _) => 0, (_, h) => h);

        var (field, x) = XSignalProcessing.Process(s, 0, 90, Settings(SweepSelection.Up, grid: new FieldGrid(300, 300, 10)));
        Assert.Equal(300, x.Single(), 9);

        // Field correction 5 Oe on the up sweep: the point recorded at 305 Oe is placed at 300 Oe.
        (field, x) = XSignalProcessing.Process(s, 5, 90, Settings(SweepSelection.Up, grid: new FieldGrid(300, 300, 10)));
        Assert.Equal(305, x.Single(), 9);
    }

    [Fact]
    public void Sample_file_runs_through_the_pipeline()
    {
        var m = FmrCsvReader.Read(Path.Combine(AppContext.BaseDirectory, "data", "MM100610_cprllH_42G.csv"));
        var s = FieldSweeps.FromMeasurement(m);
        var grid = XSignalProcessing.GuessGrid(s, 21, SweepSelection.UpAndDown);
        var settings = Settings(grid: grid);
        settings = settings with { FieldBackground = XSignalProcessing.AutoFieldBackground(s, 21, 1, settings) };

        var (field, x) = XSignalProcessing.Process(s, 21, 1, settings);

        Assert.Equal(grid.Points().Length, field.Length);
        Assert.All(x, v => Assert.True(double.IsFinite(v)));
        Assert.Equal(0, field[0] % 10, 9);

        foreach (var method in new[] { SmoothingMethod.Fft, SmoothingMethod.SavitzkyGolay })
        {
            var (smoothedField, smoothed) = XSignalProcessing.Process(s, 21, 1,
                settings with { Smoothing = SmoothingSettings.Default(method) });
            Assert.Equal(field, smoothedField);
            Assert.All(smoothed, v => Assert.True(double.IsFinite(v)));
            // Smoothing must not shrink the resonance much: the peak-to-peak stays within 20 %.
            Assert.InRange((smoothed.Max() - smoothed.Min()) / (x.Max() - x.Min()), 0.8, 1.0 + 1e-9);
        }
    }
}
