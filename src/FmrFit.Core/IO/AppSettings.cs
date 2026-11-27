using System.Globalization;
using System.Text;

namespace FmrFit.Core.IO;

/// <summary>Size shared by the main, import and fitting windows (layout pixels).</summary>
public sealed record WindowSize(double Width, double Height, bool Maximized);

/// <summary>Sweeps selected by default for a new import.</summary>
public enum DefaultSweeps
{
    UpAndDown,
    Up,
    Down,
}

/// <summary>
/// User settings and remembered state, stored in <c>fmr-fit.ini</c> (see <see cref="AppSettingsFile"/>). Plain data:
/// the app applies the values and passes them to Core as parameters.
/// </summary>
/// <param name="ImportFolder">Folder the measurement file dialog opens in; empty for the system default.</param>
/// <param name="ExportFolder">Folder the export file dialog opens in; empty for the Projects folder.</param>
/// <param name="SelectedWorkFile">File name (no folder) of the work file selected in the main window; empty for none.</param>
/// <param name="Window">Size of the main, import and fitting windows.</param>
/// <param name="MaxRedrawsPerSecond">Most plot redraws per second while a slider is dragged or an entry is stepped with the mouse wheel.</param>
/// <param name="GridStepOe">Default interpolation grid step of a new import (Oe).</param>
/// <param name="Sweeps">Sweeps selected by default for a new import.</param>
/// <param name="NormalizePower">Power normalization on by default for a new import.</param>
/// <param name="LagSearchMinS">Lower end of the sweep-lag search window (s).</param>
/// <param name="LagSearchMaxS">Upper end of the sweep-lag search window (s).</param>
/// <param name="MaxIterations">Most optimizer iterations per fit.</param>
/// <param name="DefaultLimitFactor">Limit of a new peak's widths and amplitudes, × the largest value of that kind.</param>
/// <param name="ExportDecimals">Decimal places of the values in an export for 3D plots.</param>
/// <param name="AlignmentPoints">Points averaged at each end of a spectrum when the 3D export aligns or shifts it.</param>
public sealed record AppSettings(
    string ImportFolder,
    string ExportFolder,
    string SelectedWorkFile,
    WindowSize Window,
    double MaxRedrawsPerSecond,
    double GridStepOe,
    DefaultSweeps Sweeps,
    bool NormalizePower,
    double LagSearchMinS,
    double LagSearchMaxS,
    int MaxIterations,
    double DefaultLimitFactor,
    int ExportDecimals,
    int AlignmentPoints)
{
    /// <summary>
    /// Values used when the file has none or an invalid one. The numbers match the defaults in Core
    /// (<c>FieldGrid.DefaultStep</c>, <c>SweepLagCorrection.DefaultMin/MaxDelay</c>,
    /// <c>PeakFitter.DefaultMaxIterations</c>, <c>PeakLimits.DefaultFactor</c>, <see cref="MapExportWriter.DefaultDecimals"/>,
    /// <c>ExportOperations.DefaultAlignmentPoints</c>);
    /// a test keeps them in step.
    /// </summary>
    public static AppSettings Default { get; } = new(
        ImportFolder: "",
        ExportFolder: "",
        SelectedWorkFile: "",
        Window: new WindowSize(1920, 1000, false),
        MaxRedrawsPerSecond: 60,
        GridStepOe: 10,
        Sweeps: DefaultSweeps.UpAndDown,
        NormalizePower: true,
        LagSearchMinS: 0,
        LagSearchMaxS: 5,
        MaxIterations: 2000,
        DefaultLimitFactor: 10,
        ExportDecimals: MapExportWriter.DefaultDecimals,
        AlignmentPoints: 10);
}

/// <summary>
/// Reads and writes <see cref="AppSettings"/> as an INI file: <c>[section]</c> lines, <c>key = value</c> lines and
/// <c>#</c> comment lines (one above each key with its meaning, range and default). Numbers use the invariant culture.
/// A missing or invalid value takes its default and makes the result incomplete, so the caller can rewrite the file
/// complete; unknown keys are ignored. Every key is described once in a table that drives both reading and writing.
/// </summary>
public static class AppSettingsFile
{
    public const string FileName = "fmr-fit.ini";

    public const double MinWindowWidth = 400, MinWindowHeight = 300, MaxWindowSize = 20000;

    public const int MaxAlignmentPoints = 10000;

    private const string Header =
        "# FMR-fit settings. Edit while the app is closed. A missing or invalid value is reset to its default.\n" +
        "# Numbers use '.' as decimal separator.";

    /// <summary>One setting: where it is stored, how it is described (null: shares the comment above), written and read back.</summary>
    /// <param name="Parse">The settings with this value applied, or null if the text is not a valid value.</param>
    private sealed record Key(
        string Section, string Name, string? Comment,
        Func<AppSettings, string> Format,
        Func<AppSettings, string, AppSettings?> Parse);

    private static readonly Key[] Keys =
    [
        new("state", "import_folder",
            "Folder the measurement file dialog opens in (remembered).",
            s => s.ImportFolder, (s, v) => s with { ImportFolder = v }),
        new("state", "export_folder",
            "Folder the export file dialog opens in (remembered; empty: the Projects folder).",
            s => s.ExportFolder, (s, v) => s with { ExportFolder = v }),
        new("state", "selected_work_file",
            "Work file selected in the main window, by file name in Projects/Current (remembered).",
            s => s.SelectedWorkFile, (s, v) => s with { SelectedWorkFile = v }),

        new("ui", "window_width",
            $"Size of the main, import and fitting windows in pixels (remembered; width {N(MinWindowWidth)}-{N(MaxWindowSize)}, " +
            $"height {N(MinWindowHeight)}-{N(MaxWindowSize)}). Default {N(AppSettings.Default.Window.Width)} x {N(AppSettings.Default.Window.Height)}.",
            s => N(s.Window.Width),
            (s, v) => Number(v, MinWindowWidth, MaxWindowSize) is { } x ? s with { Window = s.Window with { Width = x } } : null),
        new("ui", "window_height", null,
            s => N(s.Window.Height),
            (s, v) => Number(v, MinWindowHeight, MaxWindowSize) is { } x ? s with { Window = s.Window with { Height = x } } : null),
        new("ui", "window_maximized",
            $"Windows open maximized (remembered; true/false). Default {B(AppSettings.Default.Window.Maximized)}.",
            s => B(s.Window.Maximized),
            (s, v) => Bool(v) is { } x ? s with { Window = s.Window with { Maximized = x } } : null),
        new("ui", "max_redraws_per_second",
            $"Most plot redraws per second while a slider is dragged or an entry is wheel-stepped (1-240). Default {N(AppSettings.Default.MaxRedrawsPerSecond)}.",
            s => N(s.MaxRedrawsPerSecond),
            (s, v) => Number(v, 1, 240) is { } x ? s with { MaxRedrawsPerSecond = x } : null),

        new("import", "grid_step_oe",
            $"Interpolation grid step in Oe (0.1-10000). Default {N(AppSettings.Default.GridStepOe)}.",
            s => N(s.GridStepOe),
            (s, v) => Number(v, 0.1, 10000) is { } x ? s with { GridStepOe = x } : null),
        new("import", "sweep_selection",
            $"Sweeps used by default: up_and_down, up or down. Default {SweepsText(AppSettings.Default.Sweeps)}.",
            s => SweepsText(s.Sweeps),
            (s, v) => ParseSweeps(v) is { } x ? s with { Sweeps = x } : null),
        new("import", "normalize_power",
            $"Normalize the signal to 10 dBm by default (true/false). Default {B(AppSettings.Default.NormalizePower)}.",
            s => B(s.NormalizePower),
            (s, v) => Bool(v) is { } x ? s with { NormalizePower = x } : null),
        new("import", "lag_search_min_s",
            $"Search window for the sweep-lag estimate in seconds (min < max, -60...60). " +
            $"Default {N(AppSettings.Default.LagSearchMinS)} and {N(AppSettings.Default.LagSearchMaxS)}.",
            s => N(s.LagSearchMinS),
            (s, v) => Number(v, -60, 60) is { } x ? s with { LagSearchMinS = x } : null),
        new("import", "lag_search_max_s", null,
            s => N(s.LagSearchMaxS),
            (s, v) => Number(v, -60, 60) is { } x ? s with { LagSearchMaxS = x } : null),

        new("fitting", "max_iterations",
            $"Most optimizer iterations per fit (10-100000). Default {AppSettings.Default.MaxIterations}.",
            s => s.MaxIterations.ToString(CultureInfo.InvariantCulture),
            (s, v) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) && x is >= 10 and <= 100000
                ? s with { MaxIterations = x }
                : null),
        new("fitting", "default_limit_factor",
            "Default limit of a new peak's widths and amplitudes, as a multiple of the largest value of that kind " +
            $"(1.5-1000). Default {N(AppSettings.Default.DefaultLimitFactor)}.",
            s => N(s.DefaultLimitFactor),
            (s, v) => Number(v, 1.5, 1000) is { } x ? s with { DefaultLimitFactor = x } : null),

        new("export", "decimals",
            $"Decimal places of the values in an export for 3D plots (remembered; 0-{MapExportWriter.MaxDecimals}). " +
            $"Default {AppSettings.Default.ExportDecimals}.",
            s => s.ExportDecimals.ToString(CultureInfo.InvariantCulture),
            (s, v) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) && x is >= 0 and <= MapExportWriter.MaxDecimals
                ? s with { ExportDecimals = x }
                : null),
        new("export", "alignment_points",
            "Points averaged at each end of a spectrum when the 3D export aligns it (the straight line through the two " +
            "end averages is subtracted) or shifts it (the mean of the last points is subtracted) " +
            $"(1-{MaxAlignmentPoints}). Default {AppSettings.Default.AlignmentPoints}.",
            s => s.AlignmentPoints.ToString(CultureInfo.InvariantCulture),
            (s, v) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) && x is >= 1 and <= MaxAlignmentPoints
                ? s with { AlignmentPoints = x }
                : null),
    ];

    /// <summary>
    /// Settings from <paramref name="path"/>; a missing or unreadable file gives the defaults. Complete is false if
    /// anything had to be defaulted, i.e. the file should be rewritten.
    /// </summary>
    public static (AppSettings Settings, bool Complete) Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return (AppSettings.Default, false);
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return Read(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (AppSettings.Default, false);
        }
    }

    /// <summary>Settings read from INI text; Complete is false if any key was missing or invalid.</summary>
    public static (AppSettings Settings, bool Complete) Read(TextReader reader)
    {
        var values = new Dictionary<(string Section, string Key), string>();
        var section = "";
        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            var text = line.Trim().TrimStart('﻿');
            if (text.Length == 0 || text.StartsWith('#') || text.StartsWith(';'))
                continue;
            if (text.StartsWith('[') && text.EndsWith(']'))
            {
                section = text[1..^1].Trim().ToLowerInvariant();
                continue;
            }
            var equals = text.IndexOf('=');
            if (equals <= 0)
                continue;
            values[(section, text[..equals].Trim().ToLowerInvariant())] = text[(equals + 1)..].Trim();
        }

        var settings = AppSettings.Default;
        var complete = true;
        foreach (var key in Keys)
        {
            if (values.TryGetValue((key.Section, key.Name), out var value) && key.Parse(settings, value) is { } parsed)
                settings = parsed;
            else
                complete = false;
        }
        if (settings.LagSearchMinS >= settings.LagSearchMaxS)
        {
            settings = settings with
            {
                LagSearchMinS = AppSettings.Default.LagSearchMinS,
                LagSearchMaxS = AppSettings.Default.LagSearchMaxS,
            };
            complete = false;
        }
        return (settings, complete);
    }

    /// <summary>Writes every key, through a temporary file so an existing file is only replaced once complete.</summary>
    public static void Save(string path, AppSettings settings)
    {
        var temp = path + ".tmp";
        try
        {
            using (var writer = new StreamWriter(temp, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                Write(writer, settings);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public static void Write(TextWriter writer, AppSettings settings)
    {
        writer.NewLine = "\n";
        writer.WriteLine(Header);
        string? section = null;
        foreach (var key in Keys)
        {
            if (key.Section != section)
            {
                section = key.Section;
                writer.WriteLine();
                writer.WriteLine($"[{section}]");
            }
            if (key.Comment is not null)
                writer.WriteLine($"# {key.Comment}");
            // A value can never span lines in the file.
            var value = key.Format(settings).Replace('\r', ' ').Replace('\n', ' ').Trim();
            writer.WriteLine($"{key.Name} = {value}");
        }
    }

    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string B(bool value) => value ? "true" : "false";

    /// <summary>A finite number in [min, max] written with '.' as decimal separator; null otherwise.</summary>
    private static double? Number(string text, double min, double max) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.IsFinite(x) && x >= min && x <= max
            ? x
            : null;

    private static bool? Bool(string text) => text.ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    private static string SweepsText(DefaultSweeps sweeps) => sweeps switch
    {
        DefaultSweeps.Up => "up",
        DefaultSweeps.Down => "down",
        _ => "up_and_down",
    };

    private static DefaultSweeps? ParseSweeps(string text) => text.ToLowerInvariant() switch
    {
        "up_and_down" => DefaultSweeps.UpAndDown,
        "up" => DefaultSweeps.Up,
        "down" => DefaultSweeps.Down,
        _ => null,
    };
}
