using FmrFit.Core.Fitting;
using FmrFit.Core.IO;
using FmrFit.Core.Processing;

namespace FmrFit.Core.Tests.IO;

public class AppSettingsFileTests
{
    private static readonly AppSettings Custom = new(
        ImportFolder: @"C:\data\run 3 = final",
        ExportFolder: @"D:\plots",
        SelectedWorkFile: "MM100610_cprllH_42GHz(01).csv",
        Window: new WindowSize(1280, 720, true),
        MaxRedrawsPerSecond: 30,
        GridStepOe: 2.5,
        Sweeps: DefaultSweeps.Down,
        NormalizePower: false,
        LagSearchMinS: -2.5,
        LagSearchMaxS: 8,
        MaxIterations: 500,
        DefaultLimitFactor: 4,
        ExportDecimals: 3,
        AlignmentPoints: 7);

    private static string Text(AppSettings settings)
    {
        var writer = new StringWriter();
        AppSettingsFile.Write(writer, settings);
        return writer.ToString();
    }

    private static (AppSettings Settings, bool Complete) Read(string text) => AppSettingsFile.Read(new StringReader(text));

    /// <summary>The defaults' text with the line of <paramref name="key"/> replaced (or removed if null).</summary>
    private static string WithLine(string key, string? line) =>
        string.Join("\n", Text(AppSettings.Default).Split('\n')
            .Select(l => l.StartsWith(key + " =") ? line : l)
            .OfType<string>());

    [Fact]
    public void Written_settings_read_back_complete_and_unchanged()
    {
        var (settings, complete) = Read(Text(Custom));

        Assert.True(complete);
        Assert.Equal(Custom, settings);
        Assert.Equal((AppSettings.Default, true), Read(Text(AppSettings.Default)));
    }

    [Fact]
    public void Every_key_is_written_in_its_section_with_a_comment_above()
    {
        var lines = Text(AppSettings.Default).Split('\n');
        string[] sections = ["[state]", "[ui]", "[import]", "[fitting]", "[export]"];
        Assert.Equal(sections, lines.Where(l => l.StartsWith('[')));

        var keys = lines.Where(l => l.Contains(" = ") && !l.StartsWith('#')).ToList();
        Assert.Equal(16, keys.Count); // state 3, ui 4, import 5, fitting 2, export 2
        for (var i = 0; i < lines.Length; i++)
        {
            if (!keys.Contains(lines[i]))
                continue;
            // Comment line directly above, or a key sharing the comment of the key above it.
            Assert.True(lines[i - 1].StartsWith('#') || keys.Contains(lines[i - 1]), $"no comment above '{lines[i]}'");
        }
        Assert.Contains("window_maximized = false", lines);
        Assert.Contains("sweep_selection = up_and_down", lines);
    }

    [Fact]
    public void Empty_input_gives_the_defaults_and_is_incomplete()
    {
        Assert.Equal((AppSettings.Default, false), Read(""));
    }

    [Fact]
    public void Missing_or_unreadable_file_gives_the_defaults_and_is_incomplete()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fmrfit-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Equal((AppSettings.Default, false), AppSettingsFile.Load(Path.Combine(folder, AppSettingsFile.FileName)));
            // A folder where the file should be cannot be read as a file.
            var blocked = Path.Combine(folder, "blocked");
            Directory.CreateDirectory(Path.Combine(blocked, AppSettingsFile.FileName));
            Assert.Equal((AppSettings.Default, false), AppSettingsFile.Load(Path.Combine(blocked, AppSettingsFile.FileName)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Save_and_Load_round_trip_through_a_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fmrfit-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = ProjectFolder.SettingsPath(folder);
            AppSettingsFile.Save(path, Custom);

            Assert.Equal((Custom, true), AppSettingsFile.Load(path));
            Assert.Equal(["fmr-fit.ini"], Directory.GetFiles(folder).Select(Path.GetFileName)); // no temporary file left
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("max_redraws_per_second", "max_redraws_per_second = abc")]
    [InlineData("max_redraws_per_second", "max_redraws_per_second = 0")]
    [InlineData("max_redraws_per_second", null)]
    [InlineData("grid_step_oe", "grid_step_oe = 2,5")] // comma decimal
    [InlineData("grid_step_oe", "grid_step_oe = NaN")]
    [InlineData("window_width", "window_width = 100")]
    [InlineData("window_maximized", "window_maximized = yes")]
    [InlineData("sweep_selection", "sweep_selection = sideways")]
    [InlineData("max_iterations", "max_iterations = 2000.5")]
    [InlineData("default_limit_factor", "default_limit_factor = 1")]
    [InlineData("decimals", "decimals = 16")]
    [InlineData("decimals", "decimals = 2.5")]
    [InlineData("alignment_points", "alignment_points = 0")]
    [InlineData("alignment_points", "alignment_points = 3.5")]
    public void An_invalid_or_missing_value_takes_its_default_and_keeps_the_others(string key, string? line)
    {
        var text = Text(Custom).Split('\n').Select(l => l.StartsWith(key + " =") ? line : l).OfType<string>();

        var (settings, complete) = Read(string.Join("\n", text));

        Assert.False(complete);
        var expected = key switch
        {
            "max_redraws_per_second" => Custom with { MaxRedrawsPerSecond = AppSettings.Default.MaxRedrawsPerSecond },
            "grid_step_oe" => Custom with { GridStepOe = AppSettings.Default.GridStepOe },
            "window_width" => Custom with { Window = Custom.Window with { Width = AppSettings.Default.Window.Width } },
            "window_maximized" => Custom with { Window = Custom.Window with { Maximized = AppSettings.Default.Window.Maximized } },
            "sweep_selection" => Custom with { Sweeps = AppSettings.Default.Sweeps },
            "max_iterations" => Custom with { MaxIterations = AppSettings.Default.MaxIterations },
            "default_limit_factor" => Custom with { DefaultLimitFactor = AppSettings.Default.DefaultLimitFactor },
            "decimals" => Custom with { ExportDecimals = AppSettings.Default.ExportDecimals },
            "alignment_points" => Custom with { AlignmentPoints = AppSettings.Default.AlignmentPoints },
            _ => throw new ArgumentException(key),
        };
        Assert.Equal(expected, settings);
    }

    [Fact]
    public void A_lag_window_with_min_not_below_max_is_reset_as_a_whole()
    {
        var (settings, complete) = Read(WithLine("lag_search_min_s", "lag_search_min_s = 5"));

        Assert.False(complete);
        Assert.Equal(AppSettings.Default.LagSearchMinS, settings.LagSearchMinS);
        Assert.Equal(AppSettings.Default.LagSearchMaxS, settings.LagSearchMaxS);
    }

    [Fact]
    public void Unknown_keys_and_sections_are_ignored()
    {
        var text = Text(Custom) + "\n[extra]\nsomething = 1\n[ui]\ncolour = red\n";

        Assert.Equal((Custom, true), Read(text));
    }

    [Fact]
    public void Keys_are_found_regardless_of_spacing_case_and_byte_order_mark()
    {
        var text = "\uFEFF[UI]\n  Max_Redraws_Per_Second=45  \n";

        var (settings, _) = Read(text);

        Assert.Equal(45, settings.MaxRedrawsPerSecond);
    }

    [Fact]
    public void A_key_outside_its_section_is_not_used()
    {
        var text = Text(AppSettings.Default).Replace("[fitting]", "[elsewhere]");

        var (settings, complete) = Read(text.Replace("max_iterations = 2000", "max_iterations = 50"));

        Assert.False(complete);
        Assert.Equal(AppSettings.Default.MaxIterations, settings.MaxIterations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("gone.csv")]
    [InlineData(@"C:\elsewhere\file.csv")]
    [InlineData("a:b*c?<>|.csv")]
    public void Remembered_names_are_read_as_written_whatever_they_contain(string name)
    {
        var settings = AppSettings.Default with { SelectedWorkFile = name, ImportFolder = name };

        var (read, complete) = Read(Text(settings));

        Assert.True(complete);
        Assert.Equal(name, read.SelectedWorkFile);
        Assert.Equal(name, read.ImportFolder);
    }

    [Fact]
    public void A_value_never_spans_lines()
    {
        var settings = AppSettings.Default with { ImportFolder = "C:\\a\r\nb" };

        var (read, complete) = Read(Text(settings));

        Assert.True(complete);
        Assert.Equal("C:\\a  b", read.ImportFolder);
    }

    [Fact]
    public void Defaults_match_the_defaults_in_Core()
    {
        var d = AppSettings.Default;

        Assert.Equal(FieldGrid.DefaultStep, d.GridStepOe);
        Assert.Equal(SweepLagCorrection.DefaultMinDelay, d.LagSearchMinS);
        Assert.Equal(SweepLagCorrection.DefaultMaxDelay, d.LagSearchMaxS);
        Assert.Equal(PeakFitter.DefaultMaxIterations, d.MaxIterations);
        Assert.Equal(PeakLimits.DefaultFactor, d.DefaultLimitFactor);
        Assert.Equal(6, d.ExportDecimals);
        Assert.Equal(ExportOperations.DefaultAlignmentPoints, d.AlignmentPoints);
    }
}
