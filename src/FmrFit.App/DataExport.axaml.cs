using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FmrFit.Core.IO;
using FmrFit.Core.Processing;

namespace FmrFit.App;

/// <summary>
/// Export settings: choose work files and write them in one file. Shown modally by the main window; returns the
/// written path, or null if nothing was exported.
/// </summary>
public partial class DataExport : Window
{
    private static readonly FilePickerFileType CsvFiles = new("CSV files") { Patterns = ["*" + MapExportWriter.Extension] };

    private List<ExportFileItem> _items = [];

    /// <summary>Row last reached by the current press-and-drag; -1 when no drag is going on.</summary>
    private int _dragIndex = -1;

    /// <summary>What the current drag does to the rows it passes: the state the pressed row was switched to.</summary>
    private bool _dragSelects;

    public DataExport()
    {
        InitializeComponent();
    }

    /// <summary>The main window's files, in its order; unreadable ones and ones without a frequency are not listed.</summary>
    public IReadOnlyList<WorkFileItem> WorkFiles { get; init; } = [];

    /// <summary>Projects/Current: refused as a target, since a file there would be listed as a work file.</summary>
    public string? WorkFolder { get; init; }

    /// <summary>Start folder of the file dialog when no export folder is remembered.</summary>
    public string? ProjectsFolder { get; init; }

    public SettingsStore? Settings { get; init; }

    /// <summary>Number of spectra in the written file (0 if nothing was exported).</summary>
    public int ExportedCount { get; private set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _items = WorkFiles
            .Where(i => i.File is { Metadata.FrequencyGHz: not null })
            .Select(i => new ExportFileItem(i, i.File!))
            .ToList();
        foreach (var item in _items)
            item.PropertyChanged += (_, _) => UpdateStatus();
        FileList.ItemsSource = _items;
        DecimalsInput.Value = Settings?.Current.ExportDecimals ?? MapExportWriter.DefaultDecimals;
        // All start off on every opening, so no earlier choice changes the exported values unnoticed.
        AlignCheckBox.Content = $"Align: subtract the line through the first and last {AlignmentPoints} points of each spectrum";
        ShiftCheckBox.Content = $"Shift: subtract the mean of the last {AlignmentPoints} points of each spectrum";
        UpdateStatus();
    }

    private int Decimals => (int)Math.Round(DecimalsInput.Value);

    private int AlignmentPoints => Settings?.Current.AlignmentPoints ?? ExportOperations.DefaultAlignmentPoints;

    private ExportOperations Operations =>
        new(AlignCheckBox.IsChecked == true, AlignmentPoints, NormaliseCheckBox.IsChecked == true,
            Shift: ShiftCheckBox.IsChecked == true);

    /// <summary>Align already brings the last points' mean to 0, so Align and Shift exclude each other.</summary>
    private void AlignCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (AlignCheckBox.IsChecked == true)
            ShiftCheckBox.IsChecked = false;
    }

    private void ShiftCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (ShiftCheckBox.IsChecked == true)
            AlignCheckBox.IsChecked = false;
    }

    /// <summary>Keeps the entry a whole number and remembers it in the settings.</summary>
    private void DecimalsInput_ValueCommitted(object? sender, RoutedEventArgs e)
    {
        DecimalsInput.Value = Decimals;
        Settings?.Update(s => s with { ExportDecimals = Decimals });
    }

    private void UpdateStatus()
    {
        var selected = _items.Count(i => i.IsSelected);
        var skipped = WorkFiles.Count - _items.Count;
        // Each file is exported in the form it displays; one table holds one quantity (e.g. P or dP/dH).
        var mixed = MapExportBuilder.Mismatch(_items.Where(i => i.IsSelected).Select(i => i.File));
        StatusText.Text = mixed ?? $"{selected} of {_items.Count} selected"
            + (skipped > 0 ? $"; {skipped} file(s) unreadable or without frequency not listed" : "");
        ExportButton.IsEnabled = selected > 0 && mixed is null;
        SelectAllButton.IsEnabled = _items.Count > 0;
    }

    /// <summary>Like Num * in a file manager: deselects all if every file is selected, otherwise selects all.</summary>
    private void SelectAllButton_Click(object? sender, RoutedEventArgs e)
    {
        var select = _items.Any(i => !i.IsSelected);
        foreach (var item in _items)
            item.IsSelected = select;
    }

    private void FileList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(FileList).Properties;
        if (!properties.IsLeftButtonPressed && !properties.IsRightButtonPressed)
            return;
        var index = RowAt(e.GetPosition(FileList));
        if (index < 0)
            return;

        _dragSelects = !_items[index].IsSelected;
        _items[index].IsSelected = _dragSelects;
        _dragIndex = index;
        e.Pointer.Capture(FileList);
        e.Handled = true;
    }

    /// <summary>
    /// Applies the drag's state to every row from the last one reached to the one under the pointer, so a fast drag
    /// skips none. Above or below the list it scrolls and takes the first or last visible row.
    /// </summary>
    private void FileList_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragIndex < 0)
            return;

        var y = e.GetPosition(Scroller).Y;
        var height = Scroller.Bounds.Height;
        if (y < 0)
            Scroller.LineUp();
        else if (y >= height)
            Scroller.LineDown();
        var inList = Scroller.TranslatePoint(new Point(0, Math.Clamp(y, 0, Math.Max(0, height - 1))), FileList);
        var index = inList is { } p ? RowAt(p) : -1;
        if (index < 0 || index == _dragIndex)
            return;

        var step = Math.Sign(index - _dragIndex);
        for (var i = _dragIndex + step; i != index + step; i += step)
            _items[i].IsSelected = _dragSelects;
        _dragIndex = index;
    }

    private void FileList_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragIndex < 0)
            return;
        _dragIndex = -1;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void FileList_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _dragIndex = -1;

    /// <summary>Index of the row at height <paramref name="point"/>.Y in the list (any x); -1 if none.</summary>
    private int RowAt(Point point)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (FileList.ContainerFromIndex(i) is not { } row || row.TranslatePoint(default, FileList) is not { } top)
                continue;
            if (point.Y >= top.Y && point.Y < top.Y + row.Bounds.Height)
                return i;
        }
        return -1;
    }

    private async void ExportButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = _items.Where(i => i.IsSelected).Select(i => i.File).ToList();
        if (files.Count == 0)
            return;

        // Built before asking for a file name, so a spectrum that can't be aligned or normalised stops it early.
        MapExport map;
        try
        {
            map = MapExportBuilder.Build(files, Operations);
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = $"Not exported: {ex.Message}";
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export for 3D plots",
            SuggestedFileName = MapExportWriter.SuggestName(files),
            DefaultExtension = "csv",
            FileTypeChoices = [CsvFiles],
            SuggestedStartLocation = await StartFolderAsync(),
            ShowOverwritePrompt = true,
        });
        if (file is null)
            return;

        if (file.TryGetLocalPath() is not { } path)
        {
            StatusText.Text = "Please choose a location on this computer.";
            return;
        }
        if (!path.EndsWith(MapExportWriter.Extension, StringComparison.OrdinalIgnoreCase))
            path += MapExportWriter.Extension;

        var folder = Path.GetDirectoryName(path);
        if (WorkFolder is not null && folder is not null
            && string.Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(WorkFolder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Choose another folder: files in Projects/Current are listed as work files.";
            return;
        }

        try
        {
            MapExportWriter.Write(path, map, Decimals);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not export: {ex.Message}";
            return;
        }

        if (folder is { Length: > 0 })
            Settings?.Update(s => s with { ExportFolder = folder });
        ExportedCount = files.Count;
        Close(path);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);

    /// <summary>The remembered export folder, else the Projects folder; null (system default) if neither can be opened.</summary>
    private async Task<IStorageFolder?> StartFolderAsync()
    {
        foreach (var folder in new[] { Settings?.Current.ExportFolder, ProjectsFolder })
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                continue;
            try
            {
                if (await StorageProvider.TryGetFolderFromPathAsync(folder) is { } storage)
                    return storage;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }
        }
        return null;
    }
}
