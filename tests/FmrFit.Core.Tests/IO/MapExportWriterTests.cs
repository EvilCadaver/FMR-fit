using FmrFit.Core.IO;

namespace FmrFit.Core.Tests.IO;

public sealed class MapExportWriterTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("fmrfit-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private static readonly MapExport Map = new(
        ["MM100610_cprllH"], 30, 31.5, 1.5, [10], 2,
        [30, 30, 31.5], [100, 110, 100], [-0.25, 1e-5, 3]);

    private static string Text(MapExport map, int? decimals = null)
    {
        var writer = new StringWriter();
        MapExportWriter.Write(writer, map, decimals);
        return writer.ToString();
    }

    private static string[] DataRows(string text) => text.Split('\n')[11..^1];

    [Fact]
    public void Align_and_normalise_are_noted_with_each_spectrums_numbers()
    {
        var map = Map with
        {
            ValueColumn = "dPdH_1",
            AlignmentPoints = 10,
            Normalised = true,
            Operations = [new(30, -0.0123, 1.2e-6, 4.567), new(31.5, null, null, 2)],
        };

        var text = Text(map);

        Assert.Contains("# align = the straight line through the means of the first and the last 10 points of each spectrum is subtracted\n", text);
        Assert.Contains("# shift = off\n", text);
        Assert.Contains("# normalise = each spectrum is divided by its largest |value| (after aligning and shifting); the values have unit 1\n", text);
        Assert.Contains("# spectrum_operations = f_GHz; baseline_at_0_Oe; baseline_slope_per_Oe; divisor (value = (original - baseline_at_0_Oe - baseline_slope_per_Oe*H) / divisor; baseline in the original unit; empty = not done)\n", text);
        Assert.Contains("# spectrum = 30; -0.0123; 1.2E-06; 4.567\n# spectrum = 31.5; ; ; 2\nf_GHz,H_Oe,dPdH_1\n", text);
    }

    [Fact]
    public void Shift_is_noted_with_each_spectrums_constant_as_a_baseline_without_slope()
    {
        var text = Text(Map with { ShiftPoints = 7, Operations = [new(30, 0.5, null, null), new(31.5, -2, null, null)] });

        Assert.Contains("# align = off\n# shift = the mean of the last 7 points of each spectrum is subtracted (after aligning)\n# normalise = off\n", text);
        Assert.Contains("# spectrum = 30; 0.5; ; \n# spectrum = 31.5; -2; ; \nf_GHz,H_Oe,dPdH_mV\n", text);
    }

    [Fact]
    public void Quantity_sources_and_value_column_are_written()
    {
        var text = Text(Map with { Quantity = "P", ValueColumn = "P", Data = "dP/dH integral (4); ~P signal (6)" });

        Assert.Contains("# quantity = P\n# data = dP/dH integral (4); ~P signal (6)\n# align = off\n# shift = off\n# normalise = off\nf_GHz,H_Oe,P\n30,100,-0.25\n", text);
    }

    [Fact]
    public void Writes_a_short_header_then_frequency_field_signal_rows()
    {
        Assert.Equal(
            """
            # FMR-fit 3D export, format 1
            # sample = MM100610_cprllH
            # frequency_from_to_step_GHz = 30; 31.5; 1.5
            # field_step_Oe = 10
            # spectra = 2
            # quantity = dP/dH
            # data = dP/dH signal
            # align = off
            # shift = off
            # normalise = off
            f_GHz,H_Oe,dPdH_mV
            30,100,-0.25
            30,110,1E-05
            31.5,100,3

            """.ReplaceLineEndings("\n"),
            Text(Map));
    }

    [Fact]
    public void Single_frequency_leaves_the_step_empty_and_lists_every_field_step()
    {
        var text = Text(Map with { SampleNames = ["A", "B"], FrequencyToGHz = 30, FrequencyStepGHz = null, FieldStepsOe = [5, 10] });

        Assert.Contains("# sample = A; B\n", text);
        Assert.Contains("# frequency_from_to_step_GHz = 30; 30; \n", text);
        Assert.Contains("# field_step_Oe = 5; 10\n", text);
    }

    [Fact]
    public void Writes_to_a_file_and_replaces_an_existing_one()
    {
        var path = Path.Combine(_temp, "map.csv");
        File.WriteAllText(path, "old");

        MapExportWriter.Write(path, Map);

        Assert.Equal(Text(Map, MapExportWriter.DefaultDecimals), File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Data_values_are_rounded_to_the_chosen_decimals_without_trailing_zeros_or_exponent()
    {
        var map = Map with { FrequencyGHz = [30, 30, 30], FieldOe = [230, 230.1234567, 9876.5], Values = [-1.23456789, 1e-5, -4e-7] };

        Assert.Equal(["30,230,-1.234568", "30,230.123457,0.00001", "30,9876.5,0"], DataRows(Text(map, 6)));
        Assert.Equal(["30,230,-1", "30,230,0", "30,9877,0"], DataRows(Text(map, 0)));
        Assert.Contains("# frequency_from_to_step_GHz = 30; 31.5; 1.5\n", Text(map, 0)); // header not rounded
    }

    [Theory]
    [InlineData(2.5, 0, "3")]       // halves away from zero
    [InlineData(-2.5, 0, "-3")]
    [InlineData(-0.0000004, 6, "0")] // no "-0"
    [InlineData(123456789.123, 2, "123456789.12")]
    public void Rounded(double value, int decimals, string expected) =>
        Assert.Equal(expected, MapExportWriter.Rounded(value, decimals));

    [Fact]
    public void Rejects_decimals_out_of_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Text(Map, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Text(Map, MapExportWriter.MaxDecimals + 1));
    }

    [Fact]
    public void Rejects_columns_of_different_length() =>
        Assert.Throws<ArgumentException>(() => Text(Map with { Values = [1] }));
}
