using System.Text.RegularExpressions;
using FmrFit.Core.IO;

namespace FmrFit.Core.Tests.IO;

public class FmrCsvReaderTests
{
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");

    private static FmrMeasurement ReadText(string text) => FmrCsvReader.Read(new StringReader(text), skipLeadingRows: 0);

    [Fact]
    public void Reads_metadata_and_first_four_columns()
    {
        var m = ReadText("""
            Sample Name, S1
            Freq, 29, GHz, Power, 15, dBm, 21 degC
            SR850: f = 25 Hz
            Time,Field,X,Y,R,theta
            s,Oe,V,V,V,deg
            1.0E+0,1.5E+2,-2.0E-6,3.0E-6,9,9
            2.0E+0,1.6E+2,-2.5E-6,3.5E-6,9,9
            """);

        Assert.Equal("S1", m.Metadata.SampleName);
        Assert.Equal(29, m.Metadata.FrequencyGHz);
        Assert.Equal(15, m.Metadata.PowerDbm);
        Assert.Equal(3, m.Metadata.PreambleLines.Count);
        Assert.Equal([1.0, 2.0], m.Time);
        Assert.Equal([150.0, 160.0], m.Field);
        Assert.Equal([-2.0e-6, -2.5e-6], m.X);
        Assert.Equal([3.0e-6, 3.5e-6], m.Y);
        Assert.Equal(0, m.SkippedRepeatedRows);
    }

    [Fact]
    public void Drops_rows_whose_time_repeats_the_previous_row()
    {
        var m = ReadText("""
            Time,Field,X,Y
            1,100,1,1
            2,101,2,2
            2,101,2,2
            2,102,3,3
            3,103,4,4
            """);

        Assert.Equal([1.0, 2.0, 3.0], m.Time);
        Assert.Equal([100.0, 101.0, 103.0], m.Field);
        Assert.Equal(2, m.SkippedRepeatedRows);
    }

    [Fact]
    public void Works_without_comments_or_units_row_and_with_reordered_columns()
    {
        var m = ReadText("""
            theta,Y,X,Field,Time

            0,4,3,200,0.5
            """);

        Assert.Equal([0.5], m.Time);
        Assert.Equal([200.0], m.Field);
        Assert.Equal([3.0], m.X);
        Assert.Equal([4.0], m.Y);
        Assert.Null(m.Metadata.FrequencyGHz);
        Assert.Empty(m.Metadata.PreambleLines);
    }

    [Fact]
    public void Skips_first_five_data_rows_by_default()
    {
        var m = FmrCsvReader.Read(new StringReader("""
            Time,Field,X,Y
            s,Oe,V,V
            407,200,0,0
            1,150,0,0
            2,151,0,0
            3,150,0,0
            4,151,0,0
            5,155,0,0
            6,160,0,0
            """));

        Assert.Equal([5.0, 6.0], m.Time);
    }

    [Fact]
    public void Missing_header_throws()
    {
        Assert.Throws<FormatException>(() => ReadText("a,b,c\n1,2,3\n"));
    }

    [Fact]
    public void Bad_number_reports_line()
    {
        var ex = Assert.Throws<FormatException>(() => ReadText("""
            Time,Field,X,Y
            1,100,1,1
            2,abc,1,1
            """));
        Assert.Contains("Line 3", ex.Message);
    }

    [Fact]
    public void Short_row_throws()
    {
        Assert.Throws<FormatException>(() => ReadText("Time,Field,X,Y\n1,2,3\n"));
    }

    [Fact]
    public void Comment_may_span_several_lines_and_ends_at_lock_in_settings()
    {
        var m = ReadText("""
            Sample Name, S2
            Freq, 35, GHz, Power, 10, dBm, first line, Power, 99
            second line
            SR850: f = 25 Hz, tc = 100 ms
            Time,Field,X,Y
            1,100,1,1
            """);

        Assert.Equal("first line, Power, 99\nsecond line", m.Metadata.Comment);
        Assert.Equal(10, m.Metadata.PowerDbm); // "Power, 99" inside the comment is not metadata
        Assert.Equal(["f = 25 Hz", "tc = 100 ms"], m.Metadata.LockInSettings);
        Assert.Null(m.Metadata.LockInPhaseDeg);
    }

    [Fact]
    public void Missing_lock_in_line_leaves_comment_to_end_of_preamble()
    {
        var m = ReadText("""
            Freq, 35, GHz, Power, 10, dBm, just a note
            Time,Field,X,Y
            1,100,1,1
            """);

        Assert.Equal("just a note", m.Metadata.Comment);
        Assert.Empty(m.Metadata.LockInSettings);
    }

    [Fact]
    public void No_comment_when_nothing_follows_dBm()
    {
        var m = ReadText("""
            Freq, 35, GHz, Power, 10, dBm,
            SR850: f = 25 Hz
            Time,Field,X,Y
            1,100,1,1
            """);

        Assert.Null(m.Metadata.Comment);
        Assert.Equal(["f = 25 Hz"], m.Metadata.LockInSettings);
    }

    [Fact]
    public void Reads_42GHz_sample_file()
    {
        var m = FmrCsvReader.Read(Path.Combine(DataDir, "MM100610_cprllH_42G.csv"));

        Assert.Equal("MM100610_cprllH", m.Metadata.SampleName);
        Assert.Equal(42, m.Metadata.FrequencyGHz);
        Assert.Equal(15, m.Metadata.PowerDbm);
        Assert.Equal(3, m.Metadata.PreambleLines.Count);
        Assert.Equal("8.13x2.71x2.06, H along 8.13, c along 8.13, 21 degC", m.Metadata.Comment);
        Assert.Equal(
            ["f = 25 Hz", "phase = 96.5 deg", "output = 5 V", "sens. = 50 uV", "tc = 300 ms", "12dB/oct"],
            m.Metadata.LockInSettings);
        Assert.Equal(96.5, m.Metadata.LockInPhaseDeg);

        // 2038 data rows: the first 5 (file lines 6-10) are skipped, and 15 more repeat the
        // previous time stamp (file lines 800, 1042, 1150, 1299, 1382, 1396, 1608, 1628, 1674,
        // 1749, 1771, 1783, 1832, 1889, 2027).
        Assert.Equal(15, m.SkippedRepeatedRows);
        Assert.Equal(2038 - 5 - 15, m.Count);

        // First kept row is file line 11.
        Assert.Equal(8.127862E-1, m.Time[0]);
        Assert.Equal(1.505000E+2, m.Field[0]);
        Assert.Equal(-1.207000E-6, m.X[0]);
        Assert.Equal(-5.811490E-7, m.Y[0]);
        Assert.Equal(4.072139E+2, m.Time[^1]);
        Assert.Equal(2.016000E+2, m.Field[^1]);
    }

    public static TheoryData<string> DataFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(DataDir, "*.csv").Order())
            data.Add(Path.GetFileName(path));
        return data;
    }

    [Theory]
    [MemberData(nameof(DataFiles))]
    public void Every_sample_file_reads_with_strictly_increasing_time(string fileName)
    {
        var m = FmrCsvReader.Read(Path.Combine(DataDir, fileName));

        var expectedGHz = double.Parse(Regex.Match(fileName, @"_(\d+)G").Groups[1].Value);
        Assert.Equal(expectedGHz, m.Metadata.FrequencyGHz);
        Assert.True(m.Count > 1000);
        Assert.Equal(m.Count, m.Field.Length);
        Assert.Equal(m.Count, m.X.Length);
        Assert.Equal(m.Count, m.Y.Length);
        for (var i = 1; i < m.Count; i++)
            Assert.True(m.Time[i] > m.Time[i - 1], $"Time not increasing at point {i}");
    }
}
