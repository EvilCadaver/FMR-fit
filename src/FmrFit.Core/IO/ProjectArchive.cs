using System.IO.Compression;

namespace FmrFit.Core.IO;

/// <summary>Saves the current work files as a .zip archive.</summary>
public static class ProjectArchive
{
    public const string Extension = ".zip";
    public const string FallbackName = "project";

    /// <summary>
    /// Archives every file in <paramref name="currentFolder"/> (top level, no temporary files) into
    /// <paramref name="zipPath"/>, replacing an existing archive only once the new one is complete.
    /// Returns the number of files archived.
    /// </summary>
    public static int Save(string currentFolder, string zipPath)
    {
        var files = Directory.GetFiles(currentFolder)
            .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var temp = zipPath + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                foreach (var file in files)
                    zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
            File.Move(temp, zipPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
        return files.Count;
    }

    /// <summary>
    /// Suggested archive name (without extension): the most frequent sample name among the work files,
    /// or <see cref="FallbackName"/> if none is known.
    /// </summary>
    public static string SuggestName(IEnumerable<WorkFile> workFiles) =>
        workFiles
            .Select(f => f.Metadata.SampleName?.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .GroupBy(s => s!)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault() ?? FallbackName;
}
