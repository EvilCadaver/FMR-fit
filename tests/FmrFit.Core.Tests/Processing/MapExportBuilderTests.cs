using FmrFit.Core.IO;
using FmrFit.Core.Processing;

namespace FmrFit.Core.Tests.Processing;

public class MapExportBuilderTests
{
    internal static WorkFile File(string? sample, double? frequency, double[] field, double[] signal, double gridStep = 10) => new(
        new WorkFileSource(null, DateTimeOffset.Now),
        FmrMetadata.Empty with { SampleName = sample, FrequencyGHz = frequency },
        null, null,
        new WorkFileProcessing(0, 0, 0, 0, 0, "", 0, 0, 0, 0, 0, 0, gridStep, null, null),
        field, signal);

    [Fact]
    public void Rows_are_sorted_by_frequency_then_keep_each_spectrum_in_field_order()
    {
        var map = MapExportBuilder.Build(
        [
            File("S", 32, [100, 110], [3, 4]),
            File("S", 30, [100, 110, 120], [0, 1, 2]),
        ]);

        Assert.Equal([30, 30, 30, 32, 32], map.FrequencyGHz);
        Assert.Equal([100, 110, 120, 100, 110], map.FieldOe);
        Assert.Equal([0, 1, 2, 3, 4], map.Values);
        Assert.Equal(2, map.Spectra);
    }

    private static WorkFile Displaying(WorkFile file, string display) =>
        file with { Processing = file.Processing with { Display = display } };

    [Fact]
    public void Exports_the_displayed_column()
    {
        var stored = Displaying(File("S", 30, [100, 110], [1, 1]), "derivative") with
        {
            IntegralMvOe = [0, 10],
            DerivativeMvPerOe = [5, 6],
        };
        // A file without the calculated columns gets them computed: ∫1 dH from 100 = 0, 10.
        var old = Displaying(File("S", 31, [100, 110], [1, 1]), "integral");

        var derivative = MapExportBuilder.Build([stored]);
        var integral = MapExportBuilder.Build([old]);

        // Recorded dP/dH (the default): its derivative is d²P/dH², its integral P.
        Assert.Equal([5.0, 6], derivative.Values);
        Assert.Equal(("d²P/dH²", "d2PdH2_mV_per_Oe", "dP/dH derivative (1)"),
            (derivative.Quantity, derivative.ValueColumn, derivative.Data));
        Assert.Equal([0.0, 10], integral.Values);
        Assert.Equal(("P", "P_mV_Oe", "dP/dH integral (1)"), (integral.Quantity, integral.ValueColumn, integral.Data));
        var plain = MapExportBuilder.Build([File("S", 30, [1], [7])]);
        Assert.Equal(("dP/dH", "dPdH_mV", "dP/dH signal (1)"), (plain.Quantity, plain.ValueColumn, plain.Data));
    }

    private static WorkFile Absorption(WorkFile file) => file with { Recorded = "~P" };

    /// <summary>40 points 100…490 Oe: a tilted baseline 2 + 0.01·H with a peak of height 3 in the middle.</summary>
    private static WorkFile Tilted(double frequency, double peak = 3)
    {
        var field = Enumerable.Range(0, 40).Select(i => 100 + 10.0 * i).ToArray();
        var values = field.Select(h => 2 + 0.01 * h + (h is >= 280 and <= 300 ? peak : 0)).ToArray();
        return File("S", frequency, field, values);
    }

    [Fact]
    public void Align_subtracts_the_line_through_both_end_averages()
    {
        var map = MapExportBuilder.Build([Tilted(30)], new ExportOperations(Align: true, AlignmentPoints: 5, Normalise: false));

        // The baseline is a straight line, so the line through the end means is exactly it: only the peak is left.
        for (var i = 0; i < map.Values.Length; i++)
            Assert.Equal(map.FieldOe[i] is >= 280 and <= 300 ? 3 : 0, map.Values[i], 9);
        var o = Assert.Single(map.Operations!);
        Assert.Equal((30.0, 2.0, 0.01), (o.FrequencyGHz, Math.Round(o.BaselineIntercept!.Value, 9), Math.Round(o.BaselineSlope!.Value, 12)));
        Assert.Null(o.Divisor);
        Assert.Equal(5, map.AlignmentPoints);
        Assert.Equal("dPdH_mV", map.ValueColumn); // aligning keeps the unit
    }

    [Fact]
    public void Normalise_divides_each_spectrum_by_its_own_largest_magnitude()
    {
        var map = MapExportBuilder.Build([File("S", 30, [1, 2, 3], [1, -4, 2]), File("S", 31, [1, 2], [0.5, 0.25])],
            new ExportOperations(Align: false, AlignmentPoints: 5, Normalise: true));

        Assert.Equal([0.25, -1, 0.5, 1, 0.5], map.Values);
        Assert.Equal([4.0, 0.5], map.Operations!.Select(o => o.Divisor!.Value));
        Assert.All(map.Operations!, o => Assert.Null(o.BaselineSlope));
        Assert.True(map.Normalised);
        Assert.Equal("dPdH_1", map.ValueColumn);
        Assert.Equal("P_1", MapExportBuilder.Build([Absorption(File("S", 30, [1], [2]))], new(false, 5, true)).ValueColumn);
    }

    [Fact]
    public void Align_then_normalise_and_the_notes_give_back_the_original()
    {
        var file = Tilted(30, peak: -6);
        var map = MapExportBuilder.Build([file], new ExportOperations(Align: true, AlignmentPoints: 5, Normalise: true));

        Assert.Equal(-1, map.Values.Min(), 9); // the dip of −6 after aligning
        var o = map.Operations![0];
        for (var i = 0; i < map.Values.Length; i++)
        {
            var original = map.Values[i] * o.Divisor!.Value + o.BaselineIntercept!.Value + o.BaselineSlope!.Value * map.FieldOe[i];
            Assert.Equal(file.SignalMv[i], original, 9);
        }
    }

    [Fact]
    public void Shift_subtracts_the_mean_of_the_last_points_and_keeps_the_tilt()
    {
        var file = Tilted(30);
        var map = MapExportBuilder.Build([file], new ExportOperations(Align: false, AlignmentPoints: 5, Normalise: false, Shift: true));

        // The last 5 points are at 450…490 Oe: mean 2 + 0.01·470 = 6.7. The tilt stays, only the level moves.
        Assert.Equal(0, map.Values[^5..].Average(), 9);
        for (var i = 0; i < map.Values.Length; i++)
            Assert.Equal(file.SignalMv[i] - 6.7, map.Values[i], 9);
        var o = Assert.Single(map.Operations!);
        Assert.Equal(6.7, o.BaselineIntercept!.Value, 9);
        Assert.Null(o.BaselineSlope);
        Assert.Null(o.Divisor);
        Assert.Equal(5, map.ShiftPoints);
        Assert.Null(map.AlignmentPoints);
        Assert.Equal("dPdH_mV", map.ValueColumn); // shifting keeps the unit
    }

    [Fact]
    public void Shift_then_normalise_and_the_notes_give_back_the_original()
    {
        var file = Tilted(30, peak: -6);
        var map = MapExportBuilder.Build([file], new ExportOperations(Align: false, AlignmentPoints: 5, Normalise: true, Shift: true));

        // Before normalising: the dip at 280 Oe is 2 + 2.8 − 6 − 6.7 = −7.9, the largest magnitude.
        Assert.Equal(-1, map.Values[18], 9);
        var o = map.Operations![0];
        Assert.Equal(7.9, o.Divisor!.Value, 9);
        for (var i = 0; i < map.Values.Length; i++)
            Assert.Equal(file.SignalMv[i], map.Values[i] * o.Divisor.Value + o.BaselineIntercept!.Value, 9);
    }

    [Fact]
    public void Align_and_shift_together_note_one_baseline()
    {
        var file = Tilted(30);
        var map = MapExportBuilder.Build([file], new ExportOperations(Align: true, AlignmentPoints: 5, Normalise: false, Shift: true));

        // After aligning the last points already average 0, so the shift adds nothing.
        var o = map.Operations![0];
        Assert.Equal((2.0, 0.01), (Math.Round(o.BaselineIntercept!.Value, 9), Math.Round(o.BaselineSlope!.Value, 12)));
        Assert.Equal((5, 5), (map.AlignmentPoints, map.ShiftPoints));
    }

    [Fact]
    public void Without_operations_nothing_is_noted_and_the_values_are_unchanged()
    {
        var map = MapExportBuilder.Build([Tilted(30)]);

        Assert.Empty(map.Operations!);
        Assert.Null(map.AlignmentPoints);
        Assert.Null(map.ShiftPoints);
        Assert.False(map.Normalised);
        Assert.Equal(Tilted(30).SignalMv, map.Values);
    }

    [Fact]
    public void Spectra_that_cannot_be_aligned_or_normalised_stop_the_export()
    {
        var shortOne = File("S", 30, [1, 2, 3], [1, 2, 3]);
        var zeros = File("S", 31, [1, 2, 3], [0, 0, 0]);

        var tooShort = Assert.Throws<ArgumentException>(() => MapExportBuilder.Build([shortOne], new(true, 2, false)));
        Assert.Contains("30 GHz spectrum has 3 points", tooShort.Message);
        Assert.Throws<ArgumentException>(() => MapExportBuilder.Build([zeros], new(false, 2, true)));
        // A straight line aligns to all zeros, which can't be normalised either.
        Assert.Throws<ArgumentException>(() => MapExportBuilder.Build([File("S", 30, [1, 2, 3, 4], [1, 2, 3, 4])], new(true, 1, true)));
        var shiftTooShort = Assert.Throws<ArgumentException>(() => MapExportBuilder.Build([shortOne], new(false, 4, false, Shift: true)));
        Assert.Contains("30 GHz spectrum has 3 points", shiftTooShort.Message);
        // A constant shifts to all zeros.
        var flat = Assert.Throws<ArgumentException>(() => MapExportBuilder.Build([File("S", 30, [1, 2, 3], [5, 5, 5])], new(false, 2, true, Shift: true)));
        Assert.Contains("after shifting", flat.Message);
    }

    [Fact]
    public void Offset_is_exported_only_when_applied()
    {
        var file = File("S", 30, [100, 110], [1, 2]);
        var on = file with { Processing = file.Processing with { DisplayOffset = 5, DisplayOffsetApplied = true } };
        var off = on with { Processing = on.Processing with { DisplayOffsetApplied = false } };

        Assert.Equal([6.0, 7], MapExportBuilder.Build([on]).Values);
        Assert.Equal([1.0, 2], MapExportBuilder.Build([off]).Values);
    }

    [Fact]
    public void Integrated_dP_dH_and_recorded_P_go_together_as_P()
    {
        WorkFile[] files =
        [
            Displaying(File("S", 30, [100, 110], [1, 1]), "integral"),
            Absorption(File("S", 31, [100, 110], [3, 4])),
            Absorption(File("S", 32, [100, 110], [5, 6])),
        ];

        var map = MapExportBuilder.Build(files);

        Assert.Null(MapExportBuilder.Mismatch(files));
        Assert.Equal([0.0, 10, 3, 4, 5, 6], map.Values);
        Assert.Equal("P", map.Quantity);
        Assert.Equal("P", map.ValueColumn); // mV·Oe and mV: no common unit
        Assert.Equal("dP/dH integral (1); ~P signal (2)", map.Data);
    }

    [Fact]
    public void Files_giving_different_quantities_are_refused()
    {
        // dP/dH as recorded, its integral (P), and ~P differentiated (dP/dH, matches the first).
        WorkFile[] files =
        [
            File("S", 30, [1], [7]),
            Displaying(File("S", 31, [1], [7]), "integral"),
            Displaying(Absorption(File("S", 32, [1], [7])), "derivative"),
        ];

        Assert.Equal("The work files show different quantities: P (1), dP/dH (2). Change the display with Modify… so "
                     + "that all give the same one (dP/dH as recorded, or its integral and ~P as recorded, both give P).",
            MapExportBuilder.Mismatch(files));
        Assert.Throws<ArgumentException>(() => MapExportBuilder.Build(files));
        Assert.Null(MapExportBuilder.Mismatch([files[0], files[2]]));
        Assert.Equal("dPdH", MapExportBuilder.Build([files[0], files[2]]).ValueColumn); // mV and mV/Oe
    }

    [Fact]
    public void Equal_frequencies_keep_the_given_order()
    {
        var map = MapExportBuilder.Build([File("S", 30, [1], [7]), File("S", 30, [1], [8])]);

        Assert.Equal([7, 8], map.Values);
    }

    [Fact]
    public void Summary_has_samples_frequency_range_most_regular_step_and_field_steps()
    {
        // 30, 31, 32, 34, 35 GHz: the 2 GHz gap is the exception, 1 GHz the regular step.
        var map = MapExportBuilder.Build(
        [
            File("A", 35, [0], [0]),
            File("B", 30, [0], [0], gridStep: 5),
            File("A", 31, [0], [0]),
            File("A", 32.000000000001, [0], [0]),
            File(null, 34, [0], [0]),
        ]);

        Assert.Equal(["A", "B"], map.SampleNames);
        Assert.Equal(30, map.FrequencyFromGHz);
        Assert.Equal(35, map.FrequencyToGHz);
        Assert.Equal(1, map.FrequencyStepGHz);
        Assert.Equal([5, 10], map.FieldStepsOe);
    }

    [Fact]
    public void Unknown_grid_step_falls_back_to_the_point_spacing()
    {
        var map = MapExportBuilder.Build([File("S", 30, [0, 2.5, 5, 7.5], [0, 0, 0, 0], gridStep: 0)]);

        Assert.Equal([2.5], map.FieldStepsOe);
        Assert.Null(map.FrequencyStepGHz);
    }

    [Fact]
    public void Needs_files_with_a_frequency()
    {
        Assert.Throws<ArgumentException>(() => MapExportBuilder.Build([]));
        Assert.Throws<ArgumentException>(() => MapExportBuilder.Build([File("S", null, [0], [0])]));
    }

    [Theory]
    [InlineData(new[] { 30.0, 31, 32, 34, 35 }, 1.0)]
    [InlineData(new[] { 40.0, 30, 35, 45 }, 5.0)]     // unsorted
    [InlineData(new[] { 30.0, 30, 32, 32, 34 }, 2.0)] // repeated values are not a step
    [InlineData(new[] { 1.0, 2, 4, 6, 7 }, 1.0)]      // tie: the smaller step
    public void Most_regular_step(double[] values, double expected) =>
        Assert.Equal(expected, MapExportBuilder.MostRegularStep(values));

    [Fact]
    public void Most_regular_step_needs_two_distinct_values()
    {
        Assert.Null(MapExportBuilder.MostRegularStep([]));
        Assert.Null(MapExportBuilder.MostRegularStep([42, 42]));
    }

    [Fact]
    public void Real_measurements_give_one_block_per_frequency()
    {
        // One real file, labelled with three frequencies; X scaled by f so the blocks differ.
        var real = FmrCsvReader.Read(Path.Combine(AppContext.BaseDirectory, "data", "MM100610_cprllH_42G.csv"));
        var files = new[] { 29.0, 30.0, 33.0 }
            .Select(f =>
            {
                var measurement = real with
                {
                    Metadata = real.Metadata with { FrequencyGHz = f },
                    X = real.X.Select(x => x * f).ToArray(),
                };
                var name = $"MM100610_cprllH_{f}G.csv";
                var sweeps = FieldSweeps.FromMeasurement(measurement);
                var settings = new XProcessingSettings(
                    SweepSelection.UpAndDown, LinearBackground.None, LinearBackground.None,
                    XSignalProcessing.GuessGrid(sweeps, 0, SweepSelection.UpAndDown));
                return WorkFileFactory.Create(name, measurement, sweeps, 0, measurement.Metadata.LockInPhaseDeg ?? 0,
                    settings, normalizePower: true, DateTimeOffset.Now);
            })
            .ToList();

        var map = MapExportBuilder.Build(files);

        Assert.Equal(["MM100610_cprllH"], map.SampleNames);
        Assert.Equal((29, 33), (map.FrequencyFromGHz, map.FrequencyToGHz));
        Assert.Equal(1, map.FrequencyStepGHz);
        Assert.Equal([10], map.FieldStepsOe);
        Assert.Equal(files.Sum(f => f.FieldOe.Length), map.FieldOe.Length);
        Assert.Equal(files[1].SignalMv, map.Values.Skip(files[0].SignalMv.Length).Take(files[1].SignalMv.Length));
    }
}
