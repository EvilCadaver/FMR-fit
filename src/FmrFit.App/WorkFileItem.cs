using System.Globalization;
using FmrFit.Core.IO;

namespace FmrFit.App;

/// <summary>One row of the main window's work-file list.</summary>
/// <param name="File">The parsed work file, or null if it could not be read.</param>
public sealed record WorkFileItem(string Path, WorkFile? File)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public bool IsReadable => File is not null;

    public double? FrequencyGHz => File?.Metadata.FrequencyGHz;

    /// <summary>Frequency column in GHz (unit in the column header), shown before the (possibly shortened) file name.</summary>
    public string FrequencyText => File is null
        ? "–"
        : FrequencyGHz is { } f ? f.ToString("0.###", CultureInfo.InvariantCulture) : "?";

    public string NameText => File is null ? $"{Name} (unreadable)" : Name;
}
