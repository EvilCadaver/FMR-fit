using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FmrFit.Core.IO;
using FmrFit.Core.Processing;
using ScottPlot;
using ScottPlot.Plottables;

namespace FmrFit.App;

public partial class MainWindow : Window
{
    private const float NormalLineWidth = 1.5f;
    private const float SelectedLineWidth = 3f;

    private static readonly IPalette Palette = new ScottPlot.Palettes.Category10();

    private readonly string? _root;
    private readonly string? _workFolder;

    /// <summary>User settings (fmr-fit.ini in the root); handed to the import and fitting windows.</summary>
    private readonly SettingsStore _settings;
    private readonly Dictionary<string, Scatter> _curves = [];
    private List<WorkFileItem> _items = [];

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            _root = ProjectFolder.LocateRoot(AppContext.BaseDirectory);
            _workFolder = ProjectFolder.EnsureCurrent(_root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Cannot use the work folder: {ex.Message}";
            ImportDataButton.IsEnabled = false;
            RefreshButton.IsEnabled = false;
        }

        _settings = _root is null ? SettingsStore.InMemory() : SettingsStore.Load(ProjectFolder.SettingsPath(_root));
        _settings.ApplyWindowSize(this);
        // Apart from repairing a missing or incomplete file at start-up, the settings file is written only here:
        // closing the main window ends the program.
        Closed += (_, _) =>
        {
            _settings.CaptureWindowSize(this);
            _settings.Save();
        };

        ReloadWorkFiles(selectPath: null, selectName: _settings.Current.SelectedWorkFile);
        if (_settings.LoadError is { } error)
            StatusText.Text += $"\nCannot write the settings file: {error}";
    }

    private WorkFileItem? Selected => FileList.SelectedItem as WorkFileItem;

    /// <summary>Archives the work files in Projects/Current to a .zip (default: Projects/[sample name].zip).</summary>
    private async void SaveProjectButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_root is null || _workFolder is null)
            return;

        var projects = ProjectFolder.ProjectsPath(_root);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save project",
            SuggestedFileName = ProjectArchive.SuggestName(_items.Select(i => i.File).OfType<WorkFile>()),
            DefaultExtension = "zip",
            FileTypeChoices = [new FilePickerFileType("Zip archive") { Patterns = ["*" + ProjectArchive.Extension] }],
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(projects),
            ShowOverwritePrompt = true,
        });
        if (file is null)
            return;

        if (file.TryGetLocalPath() is not { } path)
        {
            StatusText.Text = "Please choose a location on this computer.";
            return;
        }
        if (!path.EndsWith(ProjectArchive.Extension, StringComparison.OrdinalIgnoreCase))
            path += ProjectArchive.Extension;

        try
        {
            var count = ProjectArchive.Save(_workFolder, path);
            StatusText.Text = $"Saved {count} file(s) to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not save the project: {ex.Message}";
        }
    }

    private async void ImportDataButton_Click(object? sender, RoutedEventArgs e)
    {
        // Modal: this window stays disabled until the import window is closed, but shows each saved file at once.
        var import = new DataImport { WorkFolder = _workFolder, Settings = _settings };
        _settings.ApplyWindowSize(import);
        import.WorkFileSaved += path => ReloadWorkFiles(selectPath: path);
        await import.ShowDialog<string?>(this);
        // Reload once more after closing, in case files changed by other means meanwhile.
        ReloadWorkFiles(selectPath: import.LastSavedPath ?? Selected?.Path);
    }

    private void RefreshButton_Click(object? sender, RoutedEventArgs e) => ReloadWorkFiles(Selected?.Path);

    private async void ExportButton_Click(object? sender, RoutedEventArgs e)
    {
        var export = new DataExport
        {
            WorkFiles = _items,
            WorkFolder = _workFolder,
            ProjectsFolder = _root is null ? null : ProjectFolder.ProjectsPath(_root),
            Settings = _settings,
        };
        if (await export.ShowDialog<string?>(this) is { } path)
            StatusText.Text = $"Exported {export.ExportedCount} spectra to {path}";
    }

    private async void FitButton_Click(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { File: not null } item)
            return;

        // Next in the fitting window steps through the readable work files in the order of this list.
        var readable = _items.Where(i => i.File is not null).Select(i => (i.Path, i.File!)).ToList();
        var fitting = new DataFitting
        {
            WorkFiles = readable,
            StartIndex = readable.FindIndex(r => r.Path == item.Path),
            Settings = _settings,
        };
        _settings.ApplyWindowSize(fitting);
        // Modal, like the import window.
        await fitting.ShowDialog(this);
        ReloadWorkFiles(selectPath: fitting.LastPath ?? item.Path);
    }

    private async void ModifyButton_Click(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { File: { } workFile } item)
            return;

        var modify = new DataImport
        {
            WorkFolder = _workFolder,
            ModifyWorkFile = workFile,
            ModifyPath = item.Path,
            Settings = _settings,
        };
        _settings.ApplyWindowSize(modify);
        var saved = await modify.ShowDialog<string?>(this);

        if (saved is not null)
            ReloadWorkFiles(selectPath: saved);
    }

    private async void RemoveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } item)
            return;

        var confirmed = await ConfirmDialog.Ask(this, "Remove work file",
            $"Delete \"{item.Name}\" from the work folder?\n\nThe original measurement file is not affected.", "Remove");
        if (!confirmed)
            return;

        try
        {
            ProjectFolder.DeleteWorkFile(item.Path);
            ReloadWorkFiles(selectPath: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not delete {item.Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Lists and plots every work file in the folder, sorted by frequency then name; files that cannot be read are
    /// listed but not plotted. Called at startup, after import/modify/remove, and by Refresh. Selects the file at
    /// <paramref name="selectPath"/>, or else the one named <paramref name="selectName"/> (file name only, as stored in
    /// the settings); nothing is selected if neither is listed.
    /// </summary>
    private void ReloadWorkFiles(string? selectPath, string? selectName = null)
    {
        var items = new List<WorkFileItem>();
        if (_workFolder is not null)
        {
            foreach (var path in ProjectFolder.ListWorkFiles(_workFolder))
            {
                WorkFile? file;
                try
                {
                    file = WorkFileReader.Read(path);
                }
                catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
                {
                    file = null;
                }
                items.Add(new WorkFileItem(path, file));
            }
        }

        items = items
            .OrderBy(i => i.FrequencyGHz ?? double.PositiveInfinity)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var plot = Plot.Plot;
        plot.Clear();
        _curves.Clear();
        var color = 0;
        // Each work file is shown in the form chosen at import (signal, integral or derivative).
        var views = items.Where(i => i.File is not null).Select(i => SignalCalculus.DisplayedView(i.File!)).Distinct().ToList();
        foreach (var item in items)
        {
            if (item.File is not { } file)
                continue;
            var view = SignalCalculus.DisplayedView(file);
            var line = plot.Add.ScatterLine(file.FieldOe, SignalCalculus.DisplayedColumn(file), Palette.GetColor(color++));
            line.LegendText = Path.GetFileNameWithoutExtension(item.Path)
                              + (views.Count > 1 && view != SignalView.Signal ? $" ({SignalViewText.ToText(view)})" : "");
            line.LineWidth = NormalLineWidth;
            _curves[item.Path] = line;
        }

        plot.XLabel("Field (Oe)");
        plot.YLabel(views.Count == 0 ? AxisLabel(SignalView.Signal) : string.Join(" | ", views.Order().Select(AxisLabel)));
        // The legend is built by hand in file order, so bringing a selected curve to the front doesn't reorder it.
        plot.Legend.DisplayPlottableLegendItems = false;
        plot.Legend.ManualItems.Clear();
        if (_curves.Count > 0)
            plot.ShowLegend();
        plot.Axes.AutoScale();
        Plot.Refresh();

        _items = items;
        SaveProjectButton.IsEnabled = _workFolder is not null && items.Count > 0;
        ExportButton.IsEnabled = items.Any(i => i.IsReadable);
        FileList.ItemsSource = items;
        // Plain string comparisons with the listed files: a stale or odd name just selects nothing.
        FileList.SelectedItem =
            items.FirstOrDefault(i => string.Equals(i.Path, selectPath, StringComparison.OrdinalIgnoreCase))
            ?? (string.IsNullOrEmpty(selectName)
                ? null
                : items.FirstOrDefault(i => string.Equals(Path.GetFileName(i.Path), selectName, StringComparison.OrdinalIgnoreCase)));
        UpdateSelection();

        if (_workFolder is not null)
            StatusText.Text = $"{items.Count} work file(s) in {_workFolder}";
    }

    private static string AxisLabel(SignalView view) => view switch
    {
        SignalView.Integral => "Integral (mV·Oe)",
        SignalView.Derivative => "Derivative (mV/Oe)",
        _ => "Signal (mV)",
    };

    /// <summary>
    /// Updates the view and remembers the selected file in the settings. Nothing selected (e.g. while the list is
    /// rebuilt) keeps the remembered name.
    /// </summary>
    private void FileList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateSelection();
        if (Selected is { } item)
            _settings.Update(s => s with { SelectedWorkFile = Path.GetFileName(item.Path) });
    }

    /// <summary>
    /// Enables the file buttons and draws the selected file's curve thicker and on top of the others; the other
    /// curves keep their file order.
    /// </summary>
    private void UpdateSelection()
    {
        var selected = Selected;
        FitButton.IsEnabled = selected?.IsReadable == true;
        ModifyButton.IsEnabled = selected?.IsReadable == true;
        RemoveButton.IsEnabled = selected is not null;

        var plot = Plot.Plot;
        plot.Legend.ManualItems.Clear();
        foreach (var (path, line) in _curves) // file order
        {
            line.LineWidth = path == selected?.Path ? SelectedLineWidth : NormalLineWidth;
            plot.MoveToFront(line);
            plot.Legend.ManualItems.Add(new LegendItem
            {
                LabelText = line.LegendText,
                LineColor = line.Color,
                LineWidth = line.LineWidth,
            });
        }

        if (selected is not null && _curves.TryGetValue(selected.Path, out var selectedLine))
            plot.MoveToFront(selectedLine);

        Plot.Refresh();
    }
}
