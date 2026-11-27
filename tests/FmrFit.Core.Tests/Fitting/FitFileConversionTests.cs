using FmrFit.Core.Fitting;
using FmrFit.Core.IO;
using FmrFit.Core.Processing;

namespace FmrFit.Core.Tests.Fitting;

public class FitFileConversionTests
{
    private static readonly LorentzianDerivative Lorentzian = new();
    private static readonly AsymmetricGaussianDerivative Gaussian = new();

    private static double[] Field() => Enumerable.Range(0, 801).Select(i => 2000 + 10.0 * i).ToArray();

    private static FitFile RoundTrip(FitFile file)
    {
        using var writer = new StringWriter();
        FitFileWriter.Write(writer, file);
        return FitFileReader.Read(new StringReader(writer.ToString()));
    }

    private static void AssertSamePeaks(IReadOnlyList<Peak> expected, IReadOnlyList<Peak> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Same(expected[i].Shape.GetType(), actual[i].Shape.GetType());
            Assert.Equal(expected[i].Parameters.Select(Math.Abs), actual[i].Parameters.Select(Math.Abs));
            Assert.Equal(expected[i].Parameters, actual[i].Parameters);
            Assert.Equal(expected[i].Errors, actual[i].Errors);
            Assert.Equal(expected[i].LimitsOrDefault(), actual[i].Limits);
            Assert.Equal(expected[i].Fixed is null ? null : expected[i].Fixed, actual[i].Fixed);
            Assert.Equal(expected[i].KeepHeight, actual[i].KeepHeight);
        }
    }

    [Fact]
    public void Peaks_survive_file_round_trip_with_errors_limits_fixing_and_height_keeping()
    {
        var field = Field();
        var signal = field.Select(h => Lorentzian.Evaluate(h, [5000, 250, 600, 200]) + Gaussian.Evaluate(h, [7500, 0.001, 150, 200])).ToArray();
        var peaks = new List<Peak>
        {
            PeakFitter.GuessPeak(Lorentzian, field, signal, []).WithFixed(3, true),
        };
        peaks.Add(PeakFitter.GuessPeak(Gaussian, field, signal, peaks) with { KeepHeight = false });
        var result = PeakFitter.Fit(field, signal, peaks);

        var file = FitFileConversion.ToFitFile("x.csv", result.Peaks, result, DateTimeOffset.Now);
        var back = FitFileConversion.ToPeaks(RoundTrip(file));

        AssertSamePeaks(result.Peaks, back);
        Assert.True(double.IsNaN(back[0].Errors![3])); // fixed: no error
        Assert.Equal(PeakFitter.Evaluate(field, signal, result.Peaks).Fit, PeakFitter.Evaluate(field, signal, back).Fit);
    }

    [Fact]
    public void File_carries_the_statistics_names_and_units_of_the_fit()
    {
        var field = Field();
        var signal = field.Select(h => Lorentzian.Evaluate(h, [5000, 250, 600, 0])).ToArray();
        var result = PeakFitter.Fit(field, signal, [PeakFitter.GuessPeak(Lorentzian, field, signal, [])]);

        var file = FitFileConversion.ToFitFile("sample_30GHz.csv", result.Peaks, result, DateTimeOffset.Now);

        Assert.Equal("sample_30GHz.csv", file.WorkFile);
        Assert.Equal(result.ChiSquared, file.ChiSquaredMv2);
        Assert.Equal(result.ReducedChiSquared, file.ReducedChiSquaredMv2);
        Assert.Equal(result.RSquared, file.RSquared);
        Assert.Equal(result.Points, file.Points);
        Assert.Equal(4, file.FreeParameters);
        Assert.Equal(result.Iterations, file.Iterations);
        Assert.Equal(["xc", "w", "A1", "A2"], file.Peaks[0].Parameters.Select(p => p.Name));
        Assert.Equal(["Oe", "Oe", "mV*Oe^2", "mV*Oe^2"], file.Peaks[0].Parameters.Select(p => p.Unit));
        Assert.Equal("LorentzianDerivative", file.Peaks[0].Shape);
    }

    [Fact]
    public void Fitted_column_is_stored_and_sets_the_amplitude_units()
    {
        var field = Field();
        var signal = field.Select(h => Lorentzian.Evaluate(h, [5000, 250, 600, 0])).ToArray();
        Peak[] peaks = [new(Lorentzian, [5000, 250, 600, 0]), new(Gaussian, [7500, 0.001, 150, 200])];
        var result = PeakFitter.Evaluate(field, signal, peaks);

        var integral = RoundTrip(FitFileConversion.ToFitFile("x.csv", peaks, result, DateTimeOffset.Now, data: SignalView.Integral));
        var signalFit = RoundTrip(FitFileConversion.ToFitFile("x.csv", peaks, result, DateTimeOffset.Now));

        Assert.Equal("integral", integral.Data);
        Assert.Equal(SignalView.Integral, FitFileConversion.DataOf(integral));
        Assert.Equal(["Oe", "Oe", "mV*Oe^3", "mV*Oe^3"], integral.Peaks[0].Parameters.Select(p => p.Unit));
        Assert.Equal("mV*Oe", integral.Peaks[1].Parameters[1].Unit);
        Assert.Equal(SignalView.Signal, FitFileConversion.DataOf(signalFit));
    }

    [Theory]
    [InlineData(ParameterKind.Amplitude, 0, "mV", "mV")]
    [InlineData(ParameterKind.AmplitudeFieldSquared, 0, "mV·Oe²", "mV*Oe^2")]
    [InlineData(ParameterKind.Slope, 0, "mV/Oe", "mV/Oe")]
    [InlineData(ParameterKind.AmplitudeFieldSquared, 1, "mV·Oe³", "mV*Oe^3")]
    [InlineData(ParameterKind.Slope, 1, "mV", "mV")]
    [InlineData(ParameterKind.AmplitudeFieldSquared, -1, "mV·Oe", "mV*Oe")]
    [InlineData(ParameterKind.Slope, -1, "mV/Oe²", "mV/Oe^2")]
    [InlineData(ParameterKind.Width, -1, "Oe", "Oe")]
    public void Units_follow_the_fitted_column(ParameterKind kind, int dataFieldPower, string display, string ascii)
    {
        Assert.Equal(display, ParameterUnits.OfData(kind, dataFieldPower));
        Assert.Equal(ascii, ParameterUnits.AsciiOfData(kind, dataFieldPower));
    }

    [Fact]
    public void Data_units_for_the_statistics()
    {
        Assert.Equal("mV·Oe", ParameterUnits.Data(1));
        Assert.Equal("mV²", ParameterUnits.DataSquared(0));
        Assert.Equal("(mV/Oe)²", ParameterUnits.DataSquared(-1));
    }

    [Fact]
    public void Evaluated_peaks_store_no_iterations()
    {
        var field = Field();
        var signal = field.Select(h => Lorentzian.Evaluate(h, [5000, 250, 600, 0])).ToArray();
        Peak[] peaks = [new(Lorentzian, [5000, 250, 600, 0])];

        var file = FitFileConversion.ToFitFile("x.csv", peaks, PeakFitter.Evaluate(field, signal, peaks), DateTimeOffset.Now);

        Assert.Null(file.Iterations);
        Assert.Null(file.ExitReason);
    }

    [Fact]
    public void Unknown_shapes_and_mismatched_parameters_are_rejected()
    {
        FitFileParameter P(string name) => new(name, 1, null, "Oe", 0, 10, false);
        FitFile With(FitFilePeak peak) => new("x.csv", 0, 0, 0, 0, 0, 0, null, null, DateTimeOffset.Now, [], [peak]);

        Assert.Throws<FormatException>(() => FitFileConversion.ToPeaks(With(new FitFilePeak("Voigt", false, [P("xc")]))));
        Assert.Throws<FormatException>(() => FitFileConversion.ToPeaks(With(new FitFilePeak("LorentzianDerivative", false, [P("xc"), P("w"), P("A1")]))));
        Assert.Throws<FormatException>(() => FitFileConversion.ToPeaks(With(new FitFilePeak("LorentzianDerivative", false, [P("xc"), P("w"), P("A2"), P("A1")]))));
        Assert.Throws<FormatException>(() => FitFileConversion.ToPeaks(With(new FitFilePeak("LorentzianDerivative", false,
            [P("xc"), P("w"), new("A1", 1, null, "mV*Oe^2", 5, 1, false), P("A2")]))));
    }

    [Fact]
    public void Only_formulas_of_the_shapes_used_are_written_once_each()
    {
        var field = Field();
        var signal = new double[field.Length];
        Peak[] lorentzians = [new(Lorentzian, [4000, 200, 600, 0]), new(Lorentzian, [6000, 300, 400, 0])];
        Peak[] mixed = [new(Gaussian, [7000, 0.01, 100, 100]), .. lorentzians];

        FitFile File(Peak[] peaks) => FitFileConversion.ToFitFile("x.csv", peaks, PeakFitter.Evaluate(field, signal, peaks), DateTimeOffset.Now);

        Assert.Equal([new FitFileFormula("LorentzianDerivative", Lorentzian.Formula)], File(lorentzians).Formulas);
        Assert.Equal(["AsymmetricGaussianDerivative", "LorentzianDerivative"], File(mixed).Formulas.Select(f => f.Shape));
        Assert.Empty(File([]).Formulas);
    }

    [Fact]
    public void Formulas_name_every_parameter_of_their_shape()
    {
        foreach (var shape in PeakShapes.All)
        {
            Assert.StartsWith("f(H) = ", shape.Formula);
            Assert.All(shape.ParameterNames, name => Assert.Contains(name, shape.Formula));
        }
    }

    [Fact]
    public void Fit_range_is_stored_and_restored_within_the_data()
    {
        var field = Field();
        var signal = new double[field.Length];
        Peak[] peaks = [new(Lorentzian, [5000, 250, 600, 0])];
        var data = FieldRange.Of(field)!.Value;

        var file = RoundTrip(FitFileConversion.ToFitFile("x.csv", peaks, PeakFitter.Evaluate(field, signal, peaks),
            DateTimeOffset.Now, new FieldRange(4000, 6000)));
        var whole = RoundTrip(FitFileConversion.ToFitFile("x.csv", peaks, PeakFitter.Evaluate(field, signal, peaks), DateTimeOffset.Now));

        Assert.Equal(new FieldRange(4000, 6000), FitFileConversion.RangeOf(file, data));
        Assert.Equal(data, FitFileConversion.RangeOf(whole, data));
        // A stored range reaching past the data is clipped; one outside the data falls back to the whole range.
        Assert.Equal(new FieldRange(4000, data.Max), FitFileConversion.RangeOf(file with { RangeMaxOe = 1e6 }, data));
        Assert.Equal(data, FitFileConversion.RangeOf(file with { RangeMinOe = 2e5, RangeMaxOe = 3e5 }, data));
    }

    [Fact]
    public void Shapes_are_found_by_their_stored_id()
    {
        Assert.IsType<LorentzianDerivative>(PeakShapes.ById("LorentzianDerivative"));
        Assert.IsType<AsymmetricGaussianDerivative>(PeakShapes.ById("AsymmetricGaussianDerivative"));
        Assert.Null(PeakShapes.ById("Lorentzian derivative (absorption + dispersion)"));
        Assert.Equal(PeakShapes.All.Count, PeakShapes.All.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void Real_work_file_fit_survives_saving_and_loading()
    {
        var m = FmrCsvReader.Read(Path.Combine(AppContext.BaseDirectory, "data", "MM100610_cprllH_42G.csv"));
        var field = m.Field.Where((_, i) => i % 3 == 0).ToArray();
        var signal = m.X.Where((_, i) => i % 3 == 0).Select(v => v * 1000).ToArray();
        var result = PeakFitter.Fit(field, signal, [PeakFitter.GuessPeak(Lorentzian, field, signal, [])]);

        var back = FitFileConversion.ToPeaks(RoundTrip(FitFileConversion.ToFitFile("42G.csv", result.Peaks, result, DateTimeOffset.Now)));

        Assert.Equal(result.Fit, PeakFitter.Evaluate(field, signal, back).Fit);
        Assert.Equal(result.ChiSquared, PeakFitter.Evaluate(field, signal, back).ChiSquared);
    }
}
