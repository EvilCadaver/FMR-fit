using System.Globalization;

namespace FmrFit.Core.IO;

/// <summary>
/// The folder holding work files: <c>&lt;root&gt;/Projects/Current</c>. The root is the application folder, or the
/// repository root when the application runs from a source checkout (a folder above it contains the solution file).
/// </summary>
public static class ProjectFolder
{
    public const string SolutionFileName = "FmrFit.slnx";
    public const string WorkFileExtension = ".csv";

    /// <summary>Saved peak fit of a work file: "X.csv" → "X.fit.csv" in the same folder.</summary>
    public const string FitFileExtension = ".fit.csv";

    /// <summary>
    /// Walks up from <paramref name="applicationDirectory"/> looking for <see cref="SolutionFileName"/>; returns that
    /// directory if found (running from the repository), otherwise <paramref name="applicationDirectory"/>.
    /// </summary>
    public static string LocateRoot(string applicationDirectory)
    {
        for (var dir = new DirectoryInfo(applicationDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
                return dir.FullName;
        return Path.GetFullPath(applicationDirectory);
    }

    /// <summary><c>&lt;root&gt;/Projects</c>: holds <c>Current</c> and saved project archives.</summary>
    public static string ProjectsPath(string root) => Path.Combine(root, "Projects");

    public static string CurrentPath(string root) => Path.Combine(ProjectsPath(root), "Current");

    /// <summary><c>&lt;root&gt;/fmr-fit.ini</c>: user settings (see <see cref="AppSettingsFile"/>).</summary>
    public static string SettingsPath(string root) => Path.Combine(root, AppSettingsFile.FileName);

    /// <summary>Creates <c>Projects/Current</c> under <paramref name="root"/> if needed and returns its path.</summary>
    public static string EnsureCurrent(string root)
    {
        var path = CurrentPath(root);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Work files in <paramref name="folder"/> (not their fit files), sorted by name.</summary>
    public static IReadOnlyList<string> ListWorkFiles(string folder) =>
        Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*" + WorkFileExtension)
                .Where(f => !IsFitFile(f))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    /// <summary>True for a saved peak fit ("….fit.csv").</summary>
    public static bool IsFitFile(string path) => path.EndsWith(FitFileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Path of the fit file belonging to <paramref name="workFilePath"/>: "X.csv" → "X.fit.csv".</summary>
    public static string FitFilePath(string workFilePath)
    {
        var directory = Path.GetDirectoryName(workFilePath) ?? "";
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(workFilePath) + FitFileExtension);
    }

    /// <summary>Deletes a work file and its fit file; does nothing for files that no longer exist.</summary>
    public static void DeleteWorkFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
        var fit = FitFilePath(path);
        if (File.Exists(fit))
            File.Delete(fit);
    }

    /// <summary>
    /// Unused path "[sample]_[freq]GHz.csv" in <paramref name="folder"/>, or "…(01).csv", "…(02).csv", … if taken.
    /// Falls back to <paramref name="fallbackName"/> for a missing sample and "unknown" for a missing frequency;
    /// characters not allowed in file names become "_".
    /// </summary>
    public static string UniquePath(string folder, string? sample, double? frequencyGHz, string fallbackName)
    {
        var name = string.IsNullOrWhiteSpace(sample) ? fallbackName : sample.Trim();
        var frequency = frequencyGHz is { } f ? f.ToString("0.###", CultureInfo.InvariantCulture) : "unknown";
        var stem = Sanitize($"{name}_{frequency}GHz");

        var path = Path.Combine(folder, stem + WorkFileExtension);
        for (var n = 1; File.Exists(path); n++)
            path = Path.Combine(folder, $"{stem}({n:00}){WorkFileExtension}");
        return path;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
