using FmrFit.Core.IO;

namespace FmrFit.Core.Tests.IO;

public sealed class FitFileTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("fmrfit-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private static FitFile Sample() => new(
        "MM100610_cprllH_30GHz.csv",
        ChiSquaredMv2: 4.701234567e-6,
        ReducedChiSquaredMv2: 3.868911e-9,
        RSquared: 0.997312345,
        StandardDeviationMv: Math.Sqrt(3.868911e-9),
        Points: 1223,
        FreeParameters: 7,
        Iterations: 26,
        ExitReason: "RelativePoints",
        Saved: new DateTimeOffset(2026, 11, 24, 15, 3, 0, TimeSpan.FromHours(1)),
        Formulas:
        [
            new FitFileFormula("LorentzianDerivative", "f(H) = -A1*4*w*u/(4*u^2 + w^2)^2 - A2*(w^2 - 4*u^2)/(4*u^2 + w^2)^2, with u = H - xc"),
            new FitFileFormula("AsymmetricGaussianDerivative", "f(H) = -A*u*exp(1/2 - u^2/2), with u = (H - H0)/s, s = σL for H < H0 and s = σR otherwise"),
        ],
        Peaks:
        [
            new FitFilePeak("LorentzianDerivative", KeepHeight: true,
            [
                new FitFileParameter("xc", 781.8812345678901, 1.1439, "Oe", 120, 12270, false),
                new FitFileParameter("w", 426.11, 2.4723, "Oe", 0, 5022.947341949744, false),
                new FitFileParameter("A1", 4757.3, 77.66, "mV*Oe^2", -97341.54624128505, 97341.54624128505, false),
                new FitFileParameter("A2", -3597.3, null, "mV*Oe^2", double.NegativeInfinity, 0, true),
            ]),
            new FitFilePeak("AsymmetricGaussianDerivative", KeepHeight: false,
            [
                new FitFileParameter("H0", 5000, null, "Oe", 2000, double.PositiveInfinity, false),
                new FitFileParameter("A", 0.02, null, "mV", -0.2, 0.2, false),
                new FitFileParameter("σL", 100, null, "Oe", 0, 3000, false),
                new FitFileParameter("σR", 300, null, "Oe", 0, 3000, false),
            ]),
        ]);

    private static string WriteToString(FitFile file)
    {
        using var writer = new StringWriter();
        FitFileWriter.Write(writer, file);
        return writer.ToString();
    }

    private static FitFile ReadFromString(string text) => FitFileReader.Read(new StringReader(text));

    private static void AssertSame(FitFile expected, FitFile actual)
    {
        Assert.Equal(expected with { Peaks = [], Formulas = [] }, actual with { Peaks = [], Formulas = [] });
        Assert.Equal(expected.Formulas, actual.Formulas);
        Assert.Equal(expected.Peaks.Count, actual.Peaks.Count);
        for (var i = 0; i < expected.Peaks.Count; i++)
        {
            Assert.Equal(expected.Peaks[i].Shape, actual.Peaks[i].Shape);
            Assert.Equal(expected.Peaks[i].KeepHeight, actual.Peaks[i].KeepHeight);
            Assert.Equal(expected.Peaks[i].Parameters, actual.Peaks[i].Parameters);
        }
    }

    [Fact]
    public void Write_then_read_reproduces_every_value_exactly()
    {
        AssertSame(Sample(), ReadFromString(WriteToString(Sample())));
    }

    [Fact]
    public void Statistics_follow_the_work_file_name_at_the_top()
    {
        var lines = WriteToString(Sample()).Split('\n');

        Assert.Equal("# FMR-fit peak fit, format 2", lines[0]);
        Assert.Equal("# [fit]", lines[1]);
        Assert.Equal("# work_file = MM100610_cprllH_30GHz.csv", lines[2]);
        Assert.Equal("# data = signal", lines[3]);
        Assert.StartsWith("# chi_squared_mV2 = 4.701234567E-06", lines[4]);
        Assert.StartsWith("# reduced_chi_squared_mV2 = ", lines[5]);
        Assert.StartsWith("# r_squared = 0.997312345", lines[6]);
    }

    [Fact]
    public void Fitted_column_round_trips_and_is_the_signal_in_older_files()
    {
        var integral = Sample() with { Data = "integral" };
        Assert.Equal("integral", ReadFromString(WriteToString(integral)).Data);

        var old = WriteToString(Sample()).Replace("# data = signal\n", "");
        Assert.Equal("signal", ReadFromString(old).Data);
    }

    [Fact]
    public void Table_has_one_readable_row_per_parameter()
    {
        var text = WriteToString(Sample());

        Assert.Contains("# peak 1 = LorentzianDerivative; keep_height = yes\n", text);
        Assert.Contains("# peak 2 = AsymmetricGaussianDerivative; keep_height = no\n", text);
        Assert.Contains("\npeak,parameter,value,error,unit,min,max,fixed\n", text);
        Assert.Contains("\n1,xc,781.8812345678901,1.1439,Oe,120,12270,no\n", text);
        Assert.Contains("\n1,A2,-3597.3,,mV*Oe^2,-Infinity,0,yes\n", text); // empty error, unbounded minimum
        Assert.Contains("\n2,σL,100,,Oe,0,3000,no\n", text);
    }

    [Fact]
    public void Fit_range_round_trips_and_follows_the_parameter_count()
    {
        var file = Sample() with { RangeMinOe = 4500, RangeMaxOe = 8000.5 };

        var text = WriteToString(file);
        var back = ReadFromString(text);

        Assert.Contains("# free_parameters = 7\n# fit_range_Oe = 4500; 8000.5\n", text);
        Assert.Equal(4500, back.RangeMinOe);
        Assert.Equal(8000.5, back.RangeMaxOe);
        AssertSame(file, back);
    }

    [Fact]
    public void Whole_spectrum_is_written_as_an_empty_range()
    {
        var text = WriteToString(Sample());

        Assert.Contains("# fit_range_Oe = \n", text);
        Assert.Null(ReadFromString(text).RangeMinOe);
        Assert.Null(ReadFromString(text).RangeMaxOe);
    }

    [Theory]
    [InlineData("8000; 4500")]
    [InlineData("4500")]
    [InlineData("a; b")]
    public void Invalid_fit_ranges_are_rejected(string range)
    {
        var text = WriteToString(Sample()).Replace("# fit_range_Oe = \n", $"# fit_range_Oe = {range}\n");

        Assert.Throws<FormatException>(() => ReadFromString(text));
    }

    [Fact]
    public void Formulas_are_comment_lines_between_the_statistics_and_the_peak_options()
    {
        var text = WriteToString(Sample());

        Assert.Contains("# saved = 2026-11-24T15:03:00+01:00\n# [functions]\n" +
            "# LorentzianDerivative = f(H) = -A1*4*w*u/(4*u^2 + w^2)^2 - A2*(w^2 - 4*u^2)/(4*u^2 + w^2)^2, with u = H - xc\n" +
            "# AsymmetricGaussianDerivative = f(H) = -A*u*exp(1/2 - u^2/2), with u = (H - H0)/s, s = σL for H < H0 and s = σR otherwise\n" +
            "# [peak options]\n", text);
    }

    [Fact]
    public void Without_peaks_no_functions_section_is_written()
    {
        Assert.DoesNotContain("[functions]", WriteToString(Sample() with { Formulas = [], Peaks = [] }));
    }

    [Fact]
    public void Unfitted_state_and_missing_statistics_are_written_empty()
    {
        var file = Sample() with { Iterations = null, ExitReason = null, ReducedChiSquaredMv2 = double.NaN, StandardDeviationMv = double.NaN };

        var text = WriteToString(file);
        var back = ReadFromString(text);

        Assert.Contains("# iterations = \n", text);
        Assert.Contains("# reduced_chi_squared_mV2 = \n", text);
        Assert.Null(back.Iterations);
        Assert.Null(back.ExitReason);
        Assert.True(double.IsNaN(back.ReducedChiSquaredMv2));
    }

    [Fact]
    public void A_fit_without_peaks_round_trips()
    {
        var file = Sample() with { Peaks = [], Formulas = [] };

        AssertSame(file, ReadFromString(WriteToString(file)));
    }

    [Fact]
    public void Writing_to_disk_replaces_the_file_and_leaves_no_temporary_file()
    {
        var path = Path.Combine(_temp, "x.fit.csv");
        File.WriteAllText(path, "old");

        FitFileWriter.Write(path, Sample());

        AssertSame(Sample(), FitFileReader.Read(path));
        Assert.Equal([path], Directory.GetFiles(_temp));
    }

    [Theory]
    [InlineData("# FMR-fit work file, format 1\npeak,parameter,value,error,unit,min,max,fixed\n")]
    [InlineData("# FMR-fit peak fit, format 1\n# [fit]\nH_Oe,V_mV\n")]
    [InlineData("# FMR-fit peak fit, format 1\n# [peak options]\n# peak 1 = LorentzianDerivative; keep_height = yes\npeak,parameter,value,error,unit,min,max,fixed\n1,xc,abc,,Oe,0,1,no\n")]
    [InlineData("# FMR-fit peak fit, format 1\n# [peak options]\npeak,parameter,value,error,unit,min,max,fixed\n1,xc,1,,Oe,0,1,no\n")]
    [InlineData("# FMR-fit peak fit, format 1\n# [peak options]\n# peak 1 = LorentzianDerivative; keep_height = yes\npeak,parameter,value,error,unit,min,max,fixed\n1,xc,1,,Oe,0,1,maybe\n")]
    public void Malformed_files_are_rejected(string text)
    {
        Assert.Throws<FormatException>(() => ReadFromString(text));
    }
}
