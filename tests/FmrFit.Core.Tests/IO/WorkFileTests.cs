using System.Globalization;
using FmrFit.Core.IO;

namespace FmrFit.Core.Tests.IO;

public class WorkFileTests
{
    private static WorkFile Sample(string? comment = "8.13x2.71x2.06, H along 8.13\nsecond line with \\ backslash") => new(
        new WorkFileSource("MM100610_cprllH_42G.csv", new DateTimeOffset(2026, 11, 23, 14, 5, 12, TimeSpan.FromHours(1))),
        new FmrMetadata(
            "MM100610_cprllH", 42, 15, comment,
            ["f = 25 Hz", "phase = 96.5 deg", "tc = 300 ms", "12dB/oct"],
            96.5,
            ["Sample Name, MM100610_cprllH", "Freq, 42, GHz, Power, 15, dBm,8.13x2.71x2.06", "", "SR850: f = 25 Hz, phase = 96.5 deg"]),
        50.0, -50.01,
        new WorkFileProcessing(5, 15, 21.0, 97.4, 0.9, "up&down", 0, 0, 3.7888e-6, -5.4371e-10, 230, 10190, 10, 10, 0.31622776601683794),
        [230.0, 240, 250.5],
        [-5.0123, -5.0011, 1e-7]);

    private static string WriteToString(WorkFile file)
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        WorkFileWriter.Write(writer, file);
        return writer.ToString();
    }

    private static WorkFile RoundTrip(WorkFile file) => WorkFileReader.Read(new StringReader(WriteToString(file)));

    [Fact]
    public void Round_trip_keeps_header_values_and_data()
    {
        var original = Sample();

        var read = RoundTrip(original);

        Assert.Equal(original.Source, read.Source);
        Assert.Equal(original.Processing, read.Processing);
        Assert.Equal(original.SweepRateUpOePerS, read.SweepRateUpOePerS);
        Assert.Equal(original.SweepRateDownOePerS, read.SweepRateDownOePerS);
        Assert.Equal(original.FieldOe, read.FieldOe);
        Assert.Equal(original.SignalMv, read.SignalMv);

        var m = read.Metadata;
        Assert.Equal("MM100610_cprllH", m.SampleName);
        Assert.Equal(42, m.FrequencyGHz);
        Assert.Equal(15, m.PowerDbm);
        Assert.Equal(96.5, m.LockInPhaseDeg);
        Assert.Equal(original.Metadata.LockInSettings, m.LockInSettings);
    }

    [Fact]
    public void Multi_line_comment_survives_on_a_single_header_line()
    {
        var text = WriteToString(Sample());

        Assert.Contains("# comment = 8.13x2.71x2.06, H along 8.13\\nsecond line with \\\\ backslash\n", text);
        Assert.Equal(Sample().Metadata.Comment, RoundTrip(Sample()).Metadata.Comment);
    }

    [Fact]
    public void Original_header_lines_are_kept_verbatim()
    {
        Assert.Equal(Sample().Metadata.PreambleLines, RoundTrip(Sample()).Metadata.PreambleLines);
    }

    [Fact]
    public void Missing_values_and_power_normalization_off_round_trip()
    {
        var file = Sample(comment: null) with
        {
            Metadata = new FmrMetadata(null, null, null, null, [], null, []),
            SweepRateUpOePerS = null,
            Processing = Sample().Processing with { PowerReferenceDbm = null, PowerFactor = null },
        };

        var text = WriteToString(file);
        var read = WorkFileReader.Read(new StringReader(text));

        Assert.Contains("# power_factor = off\n", text);
        Assert.Null(read.Metadata.SampleName);
        Assert.Null(read.Metadata.FrequencyGHz);
        Assert.Null(read.Metadata.Comment);
        Assert.Empty(read.Metadata.LockInSettings);
        Assert.Null(read.SweepRateUpOePerS);
        Assert.Equal(file.Processing, read.Processing);
    }

    [Fact]
    public void Smoothing_round_trips_and_is_off_when_missing()
    {
        var file = Sample() with
        {
            Processing = Sample().Processing with { Smoothing = "savitzky-golay", SmoothingPoints = 7, SmoothingPolynomialOrder = 3 },
        };

        var text = WriteToString(file);
        Assert.Contains("# smoothing = savitzky-golay\n# smoothing_points = 7\n# smoothing_polynomial_order = 3\n", text);
        Assert.Equal(file.Processing, WorkFileReader.Read(new StringReader(text)).Processing);

        // Work files written before smoothing existed have no smoothing entries.
        var old = string.Join('\n', WriteToString(Sample()).Split('\n').Where(l => !l.StartsWith("# smoothing")));
        var read = WorkFileReader.Read(new StringReader(old)).Processing;
        Assert.Equal(("off", (int?)null, (int?)null), (read.Smoothing, read.SmoothingPoints, read.SmoothingPolynomialOrder));
    }

    [Fact]
    public void Integral_and_derivative_columns_and_display_round_trip()
    {
        var file = Sample() with
        {
            Processing = Sample().Processing with { Display = "derivative" },
            IntegralMvOe = [0, -50.067, 1.25e-3],
            DerivativeMvPerOe = [1.1e-3, 0.5, -2e-9],
        };

        var text = WriteToString(file);
        var read = WorkFileReader.Read(new StringReader(text));

        Assert.Contains("# display = derivative\n", text);
        Assert.Contains(WorkFileWriter.CalculusHeader + "\n230,-5.0123,0,0.0011\n", text);
        Assert.Equal(file.Processing, read.Processing);
        Assert.Equal(file.SignalMv, read.SignalMv);
        Assert.Equal(file.IntegralMvOe, read.IntegralMvOe);
        Assert.Equal(file.DerivativeMvPerOe, read.DerivativeMvPerOe);
    }

    [Fact]
    public void Display_offset_round_trips_after_the_display_entry()
    {
        var file = Sample() with
        {
            Processing = Sample().Processing with { Display = "integral", DisplayOffset = 1523.7654321, DisplayOffsetApplied = true },
            IntegralMvOe = [0, 1, 2],
            DerivativeMvPerOe = [0, 0, 0],
        };

        var text = WriteToString(file);

        Assert.Contains("# display = integral\n# display_offset = 1523.7654321\n# display_offset_applied = yes\n", text);
        Assert.Equal(file.Processing, WorkFileReader.Read(new StringReader(text)).Processing);
        Assert.Contains("# display_offset = 0\n# display_offset_applied = no\n", WriteToString(Sample()));
    }

    [Fact]
    public void Recorded_signal_round_trips_in_the_measurement_section()
    {
        var text = WriteToString(Sample() with { Recorded = "~P" });

        Assert.Contains("# sample = MM100610_cprllH\n# recorded_signal = ~P\n", text);
        Assert.Equal("~P", WorkFileReader.Read(new StringReader(text)).Recorded);
    }

    [Fact]
    public void Two_column_files_without_display_read_as_signal_only()
    {
        var old = string.Join('\n', WriteToString(Sample()).Split('\n')
            .Where(l => !l.StartsWith("# display") && !l.StartsWith("# recorded_signal")));

        var read = WorkFileReader.Read(new StringReader(old));

        Assert.Equal("signal", read.Processing.Display);
        Assert.Equal("dP/dH", read.Recorded);
        Assert.Equal((0.0, false), (read.Processing.DisplayOffset, read.Processing.DisplayOffsetApplied));
        Assert.Null(read.IntegralMvOe);
        Assert.Null(read.DerivativeMvPerOe);
        Assert.Equal(Sample().SignalMv, read.SignalMv);
    }

    [Fact]
    public void Integral_without_derivative_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => WriteToString(Sample() with { IntegralMvOe = [1.0, 2, 3] }));
        Assert.Throws<FormatException>(() => WorkFileReader.Read(new StringReader(
            WriteToString(Sample() with { IntegralMvOe = [1.0, 2, 3], DerivativeMvPerOe = [1.0, 2, 3] })
                .Replace("\n240,-5.0011,2,2\n", "\n240,-5.0011\n"))));
    }

    [Fact]
    public void Data_part_is_two_numeric_columns_after_hash_comments()
    {
        var lines = WriteToString(Sample()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var data = lines.SkipWhile(l => l.StartsWith('#')).ToArray();

        Assert.Equal(WorkFileWriter.DataHeader, data[0]);
        Assert.All(data[1..], l =>
        {
            var cells = l.Split(',');
            Assert.Equal(2, cells.Length);
            Assert.All(cells, c => double.Parse(c, CultureInfo.InvariantCulture));
        });
        Assert.StartsWith(WorkFileWriter.Signature, lines[0]);
    }

    [Fact]
    public void Rejects_files_without_signature()
    {
        Assert.Throws<FormatException>(() => WorkFileReader.Read(new StringReader("Time,Field,X,Y\n1,2,3,4\n")));
    }

    [Fact]
    public void Writes_reads_and_replaces_a_file_on_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fmrfit-{Guid.NewGuid():N}.csv");
        try
        {
            WorkFileWriter.Write(path, Sample());
            Assert.Equal(Sample().SignalMv, WorkFileReader.Read(path).SignalMv);

            var replacement = Sample() with { SignalMv = [1.0, 2, 3] };
            WorkFileWriter.Write(path, replacement);
            Assert.Equal([1.0, 2, 3], WorkFileReader.Read(path).SignalMv);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Source_full_path_round_trips()
    {
        var file = Sample() with { Source = Sample().Source with { FilePath = @"C:\data\run 1\MM100610_cprllH_42G.csv" } };

        Assert.Equal(file.Source.FilePath, RoundTrip(file).Source.FilePath);
    }
}
