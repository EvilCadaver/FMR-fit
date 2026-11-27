using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FmrFit.Core.IO;
using FmrFit.Core.Processing;
using ScottPlot;

namespace FmrFit.App;

public partial class DataImport : Window
{
    private static readonly FilePickerFileType CsvFiles = new("CSV files")
    {
        Patterns = ["*.csv"],
        MimeTypes = ["text/csv"],
    };

    private static readonly IPalette Palette = new ScottPlot.Palettes.Category10();

    private string? _fileName;
    private string? _sourcePath;
    private FmrMeasurement? _measurement;
    private FieldSweeps? _sweeps;
    private double _filePhase;
    private bool _settingInputs;

    /// <summary>Rate limit of the redraws after an entry changes (e.g. mouse-wheel steps).</summary>
    private readonly RedrawThrottle _throttle;

    /// <summary>Source file and settings that make up a work file, compared to detect unsaved changes.</summary>
    private sealed record ImportState(string? Source, double Correction, double Phase, XProcessingSettings Processing,
        RecordedSignal Recorded);

    /// <summary>
    /// State matching the work file on disk: the one being modified as loaded, or the last one saved; null if none.
    /// </summary>
    private ImportState? _savedState;

    /// <summary>Set once the user agreed to discard, so the repeated close goes through.</summary>
    private bool _closeConfirmed;

    /// <summary>Set while the discard question is open, so a second close request doesn't ask again.</summary>
    private bool _askingToClose;

    public DataImport()
    {
        InitializeComponent();
        _throttle = new RedrawThrottle(() => Redraw(autoScale: false), () => CurrentSettings.MaxRedrawsPerSecond);
        SetAxisLabels();
        Plot.Refresh();
        Opened += (_, _) =>
        {
            // "Next" (save and import another file) only makes sense for new imports, not when modifying a file.
            NextButton.IsVisible = ModifyPath is null;
            if (ModifyPath is null)
                ApplyImportDefaults();
            LoadWorkFileToModify();
        };
        Closed += (_, _) =>
        {
            _throttle.Stop();
            Settings?.CaptureWindowSize(this);
        };
    }

    /// <summary>User settings: import defaults, the folder the file dialog opens in, the window size.</summary>
    public SettingsStore? Settings { get; init; }

    private AppSettings CurrentSettings => Settings?.Current ?? AppSettings.Default;

    /// <summary>Grid step, sweep selection and power normalization of a new import, from the settings.</summary>
    private void ApplyImportDefaults()
    {
        var s = CurrentSettings;
        GridStepInput.Value = s.GridStepOe;
        _settingInputs = true;
        SweepSelector.SelectedIndex = s.Sweeps switch
        {
            DefaultSweeps.Up => 1,
            DefaultSweeps.Down => 2,
            _ => 0,
        };
        NormalizePowerCheckBox.IsChecked = s.NormalizePower;
        _settingInputs = false;
        ApplyOffsetDefault();
    }

    /// <summary>
    /// Folder where Finish/Next save new work files. Closing returns the path saved by Finish, or null; files saved
    /// earlier with Next are in <see cref="LastSavedPath"/>.
    /// </summary>
    public string? WorkFolder { get; init; }

    /// <summary>The most recent work file saved by this window (by Finish or Next), or null.</summary>
    public string? LastSavedPath { get; private set; }

    /// <summary>Raised with the path after each work file is written (Finish or Next), while the window is still open.</summary>
    public event Action<string>? WorkFileSaved;

    /// <summary>
    /// Work file to modify: its source measurement is loaded with its processing settings, and Finish overwrites
    /// <see cref="ModifyPath"/> instead of creating a new file.
    /// </summary>
    public WorkFile? ModifyWorkFile { get; init; }

    /// <summary>Path of <see cref="ModifyWorkFile"/>.</summary>
    public string? ModifyPath { get; init; }

    private void LoadWorkFileToModify()
    {
        if (ModifyWorkFile is null || ModifyPath is null)
            return;

        Title = $"FMR-fit – Modify {Path.GetFileName(ModifyPath)}";
        _settingInputs = true;
        RecordedSelector.SelectedIndex =
            RecordedSignalText.Parse(ModifyWorkFile.Recorded) == RecordedSignal.Absorption ? 1 : 0;
        _settingInputs = false;
        var source = ModifyWorkFile.Source.FilePath;
        if (string.IsNullOrEmpty(source) || !Path.IsPathRooted(source) || !File.Exists(source))
        {
            StatusText.Text = $"Original measurement not found ({source ?? "no path stored"}). Use Open… to select it.";
            return;
        }

        try
        {
            LoadMeasurement(Path.GetFileName(source), source, FmrCsvReader.Read(source), ModifyWorkFile.Processing);
            _savedState = CurrentState();
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not read {source}: {ex.Message}";
        }
    }

    private void FinishButton_Click(object? sender, RoutedEventArgs e)
    {
        if (SaveWorkFile() is { } path)
            Close(path);
    }

    /// <summary>Saves the current import, clears the window and asks for the next measurement file.</summary>
    private async void NextButton_Click(object? sender, RoutedEventArgs e)
    {
        if (SaveWorkFile() is not { } path)
            return;

        ClearMeasurement();
        StatusText.Text = $"Saved {Path.GetFileName(path)}. Open the next measurement file.";
        await OpenMeasurementAsync();
    }

    /// <summary>Closes without saving; <see cref="OnClosing"/> asks first if there is unsaved work.</summary>
    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);

    private ImportState CurrentState() =>
        new(_sourcePath, Correction, PhaseInput.Value, ProcessingSettings, SelectedRecorded);

    /// <summary>
    /// A loaded measurement whose settings differ from the work file on disk: a new import not saved yet, or a work
    /// file being modified whose settings (or source file) were changed.
    /// </summary>
    private bool HasUnsavedWork => _measurement is not null && CurrentState() != _savedState;

    /// <summary>
    /// Closing by Cancel, the window's close button or Alt+F4 with unsaved work: the close is cancelled and the user is
    /// asked; on confirmation the window closes. Closing because the application shuts down is not held.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closeConfirmed || e.CloseReason != WindowCloseReason.WindowClosing || !HasUnsavedWork)
            return;
        e.Cancel = true;
        ConfirmClose();
    }

    private async void ConfirmClose()
    {
        if (_askingToClose)
            return;
        _askingToClose = true;
        try
        {
            var question = ModifyPath is not null
                ? $"Discard the changed settings of \"{Path.GetFileName(ModifyPath)}\" and close?"
                : $"\"{_fileName}\" has not been saved as a work file. Discard it and close?";
            if (!await ConfirmDialog.Ask(this, "Discard import", question, "Discard"))
                return;
            _closeConfirmed = true;
            Close(null);
        }
        finally
        {
            _askingToClose = false;
        }
    }

    /// <summary>
    /// Writes the work file (a new unique file, or <see cref="ModifyPath"/> when modifying) and returns its path;
    /// on failure shows the reason in the status line and returns null.
    /// </summary>
    private string? SaveWorkFile()
    {
        if (_measurement is null || _sweeps is null || (WorkFolder is null && ModifyPath is null))
            return null;

        var workFile = WorkFileFactory.Create(_sourcePath, _measurement, _sweeps, Correction, PhaseInput.Value,
            ProcessingSettings, NormalizePowerCheckBox.IsChecked == true, DateTimeOffset.Now, SelectedRecorded);
        if (workFile.FieldOe.Length == 0)
        {
            StatusText.Text = "Nothing to save: the processed X is empty. Check the interpolation range.";
            return null;
        }

        var fallbackName = Path.GetFileNameWithoutExtension(_fileName) ?? "measurement";
        try
        {
            string path;
            if (ModifyPath is not null)
            {
                path = ModifyPath;
            }
            else
            {
                Directory.CreateDirectory(WorkFolder!);
                path = ProjectFolder.UniquePath(WorkFolder!, _measurement.Metadata.SampleName,
                    _measurement.Metadata.FrequencyGHz, fallbackName);
            }
            WorkFileWriter.Write(path, workFile);
            _savedState = CurrentState();
            LastSavedPath = path;
            WorkFileSaved?.Invoke(path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not save the work file: {ex.Message}";
            return null;
        }
    }

    /// <summary>Forgets the loaded measurement so it cannot be saved twice; inputs are reset by the next load.</summary>
    private void ClearMeasurement()
    {
        _fileName = null;
        _sourcePath = null;
        _measurement = null;
        _sweeps = null;
        SetEnabledForMeasurement();
        ShowInfo(FmrMetadata.Empty, null);
        Redraw(autoScale: true);
    }

    private double Correction => CorrectionInput.Value;

    /// <summary>Rotation relative to the phase the file was recorded with.</summary>
    private double PhaseDelta => PhaseInput.Value - _filePhase;

    private SweepSelection Selection => SweepSelector.SelectedIndex switch
    {
        1 => SweepSelection.Up,
        2 => SweepSelection.Down,
        _ => SweepSelection.UpAndDown,
    };

    private XProcessingSettings ProcessingSettings => new(
        Selection,
        new LinearBackground(TimeInterceptInput.Value, TimeSlopeInput.Value),
        new LinearBackground(FieldInterceptInput.Value, FieldSlopeInput.Value),
        new FieldGrid(GridFromInput.Value, GridToInput.Value, GridStepInput.Value),
        PowerFactor,
        SmoothingFromInputs,
        SelectedView,
        ApplyOffset,
        OffsetInput.Value);

    private bool ApplyOffset => ApplyOffsetCheckBox.IsChecked == true;

    /// <summary>
    /// "Offset to 0" on for an integral or a ~P measurement, off otherwise; when on, the offset is set automatically
    /// (as by A), when off it is 0. Used for a new import and when the recorded signal or the shown form is changed
    /// (an offset in the old form's units means nothing in the new one).
    /// </summary>
    private void ApplyOffsetDefault()
    {
        _settingInputs = true;
        ApplyOffsetCheckBox.IsChecked = SignalCalculus.DefaultApplyOffset(SelectedRecorded, SelectedView);
        _settingInputs = false;
        OffsetInput.Value = 0;
        if (ApplyOffset)
            EstimateOffset();
        UpdateOffsetEnabled();
    }

    /// <summary>The entry and A work only while the offset is applied; switched off, the value is kept unused.</summary>
    private void UpdateOffsetEnabled()
    {
        OffsetInput.IsEnabled = ApplyOffset;
        AutoOffsetButton.IsEnabled = ApplyOffset;
    }

    /// <summary>Offset that puts the minimum of the shown form (without offset) at 0; unchanged without data.</summary>
    private void EstimateOffset()
    {
        if (_sweeps is null)
            return;
        var settings = ProcessingSettings;
        var (field, x) = XSignalProcessing.Process(_sweeps, Correction, PhaseDelta, settings);
        if (field.Length > 0)
            OffsetInput.Value = SignalCalculus.ZeroMinimumOffset(SignalCalculus.View(field, x, settings.Display));
    }

    /// <summary>Applies or stops applying the offset; its value is kept either way.</summary>
    private void ApplyOffsetCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_settingInputs || OffsetInput is null)
            return;
        UpdateOffsetEnabled();
        if (_measurement is not null)
            Redraw(autoScale: false);
    }

    private void AutoOffsetButton_Click(object? sender, RoutedEventArgs e)
    {
        EstimateOffset();
        Redraw(autoScale: false);
    }

    private void OffsetInput_ValueCommitted(object? sender, RoutedEventArgs e)
    {
        if (_measurement is not null)
            _throttle.Request();
    }

    private SignalView SelectedView => SignalSelector.SelectedIndex switch
    {
        1 => SignalView.Integral,
        2 => SignalView.Derivative,
        _ => SignalView.Signal,
    };

    /// <summary>Power normalization of the processed X only; 1 when off or the power is unknown.</summary>
    private double PowerFactor =>
        NormalizePowerCheckBox.IsChecked == true && _measurement?.Metadata.PowerDbm is { } p
            ? PowerNormalization.Factor(p)
            : 1;

    private async void OpenButton_Click(object? sender, RoutedEventArgs e) => await OpenMeasurementAsync();

    /// <summary>Asks for a measurement file and loads it; does nothing if the dialog is cancelled.</summary>
    private async Task OpenMeasurementAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open FMR measurement",
            AllowMultiple = false,
            // The first entry is the filter selected when the dialog opens.
            FileTypeFilter = [CsvFiles, FilePickerFileTypes.All],
            SuggestedStartLocation = await ImportFolderAsync(),
        });

        if (files.Count == 0)
            return;

        var file = files[0];
        FmrMeasurement measurement;
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            measurement = FmrCsvReader.Read(reader);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not read {file.Name}: {ex.Message}";
            return;
        }

        var localPath = file.TryGetLocalPath();
        if (Path.GetDirectoryName(localPath) is { Length: > 0 } folder)
            Settings?.Update(s => s with { ImportFolder = folder });
        LoadMeasurement(file.Name, localPath ?? file.Name, measurement);
    }

    /// <summary>
    /// The remembered import folder for the file dialog; null (the system default) if none is stored, it no longer
    /// exists or it cannot be opened.
    /// </summary>
    private async Task<IStorageFolder?> ImportFolderAsync()
    {
        var folder = CurrentSettings.ImportFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return null;
        try
        {
            return await StorageProvider.TryGetFolderFromPathAsync(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <param name="restore">Processing to restore (when modifying a work file); null runs the automatic estimates.</param>
    private void LoadMeasurement(string fileName, string sourcePath, FmrMeasurement measurement,
        WorkFileProcessing? restore = null)
    {
        _fileName = fileName;
        _sourcePath = sourcePath;
        _measurement = measurement;
        // The phase the data were recorded with is the "zero": showing it means no rotation.
        _filePhase = measurement.Metadata.LockInPhaseDeg ?? 0;
        StatusText.Text = $"{fileName}: {measurement.Count} points, {measurement.SkippedRepeatedRows} repeated rows skipped";

        try
        {
            _sweeps = FieldSweeps.FromMeasurement(measurement);
        }
        catch (InvalidOperationException ex)
        {
            _sweeps = null;
            StatusText.Text += $". No sweep correction: {ex.Message}";
        }

        ShowInfo(measurement.Metadata, _sweeps);
        SetEnabledForMeasurement();
        var lagFailed = false;

        if (restore is not null && _sweeps is not null)
        {
            RestoreProcessing(restore);
        }
        else
        {
            // Order matters: each estimate uses the ones before it.
            if (_sweeps is not null)
                lagFailed = !EstimateCorrection();
            else
                CorrectionInput.Value = 0;
            EstimatePhase();
            TimeInterceptInput.Value = 0;
            TimeSlopeInput.Value = 0;
            GuessGrid();
            // No automatic field background on open: only the Auto button estimates it.
            FieldInterceptInput.Value = 0;
            FieldSlopeInput.Value = 0;
            ApplyOffsetDefault();
        }

        Redraw(autoScale: true);
        if (lagFailed)
            WarnCorrectionFailed();
    }

    /// <summary>Enables the controls that apply to the loaded measurement (all off when none is loaded).</summary>
    private void SetEnabledForMeasurement()
    {
        var loaded = _measurement is not null;
        var hasSweeps = _sweeps is not null;
        var canSave = hasSweeps && (WorkFolder is not null || ModifyPath is not null);

        NormalizePowerCheckBox.IsEnabled = _measurement?.Metadata.PowerDbm is not null;
        CorrectionInput.IsEnabled = hasSweeps;
        AutoCorrectionButton.IsEnabled = hasSweeps;
        ResetCorrectionButton.IsEnabled = hasSweeps;
        PhaseInput.IsEnabled = loaded;
        AutoPhaseButton.IsEnabled = loaded;
        ResetPhaseButton.IsEnabled = loaded;
        PhaseMinus90Button.IsEnabled = loaded;
        PhasePlus90Button.IsEnabled = loaded;
        ProcessedPanel.IsEnabled = hasSweeps;
        SmoothingPanel.IsEnabled = hasSweeps;
        OffsetRow.IsEnabled = hasSweeps;
        FinishButton.IsEnabled = canSave;
        NextButton.IsEnabled = canSave && ModifyPath is null;
    }

    /// <summary>Puts the settings stored in a work file back into the inputs (no automatic estimates).</summary>
    private void RestoreProcessing(WorkFileProcessing p)
    {
        CorrectionInput.Value = p.SweepCorrectionOe;
        PhaseInput.Value = p.PhaseDeg;
        TimeInterceptInput.Value = p.TimeBackgroundIntercept;
        TimeSlopeInput.Value = p.TimeBackgroundSlope;
        FieldInterceptInput.Value = p.FieldBackgroundIntercept;
        FieldSlopeInput.Value = p.FieldBackgroundSlope;
        GridStepInput.Value = p.GridStepOe;
        GridFromInput.Value = p.GridFromOe;
        GridToInput.Value = p.GridToOe;

        _settingInputs = true;
        SweepSelector.SelectedIndex = SweepSelectionText.Parse(p.Sweeps) switch
        {
            SweepSelection.Up => 1,
            SweepSelection.Down => 2,
            _ => 0,
        };
        NormalizePowerCheckBox.IsChecked = p.PowerFactor is not null;
        SignalSelector.SelectedIndex = SignalViewText.Parse(p.Display) switch
        {
            SignalView.Integral => 1,
            SignalView.Derivative => 2,
            _ => 0,
        };
        ApplyOffsetCheckBox.IsChecked = p.DisplayOffsetApplied;
        _settingInputs = false;
        UpdateOffsetEnabled();
        // Stored in the work file's mV-based unit; the entry is in V-based units like the plot.
        OffsetInput.Value = p.DisplayOffset / 1000;
        SetSmoothingInputs(SmoothingSettings.FromWorkFile(p));
    }

    private void NormalizePowerCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (!_settingInputs && _measurement is not null)
            Redraw(autoScale: false);
    }

    private void ShowInfo(FmrMetadata info, FieldSweeps? sweeps)
    {
        const string missing = "–";
        FrequencyText.Text = info.FrequencyGHz is { } f ? $"{f.ToString(CultureInfo.InvariantCulture)} GHz" : missing;
        PowerText.Text = info.PowerDbm is { } p ? $"{p.ToString(CultureInfo.InvariantCulture)} dBm" : missing;
        SweepRateText.Text = sweeps is null
            ? missing
            : $"up {sweeps.UpRate:F1} Oe/s\ndown {sweeps.DownRate:F1} Oe/s";
        SampleText.Text = info.SampleName ?? missing;
        CommentText.Text = info.Comment ?? missing;
        LockInText.Text = info.LockInSettings.Count > 0 ? string.Join('\n', info.LockInSettings) : missing;
    }

    private void AutoCorrectionButton_Click(object? sender, RoutedEventArgs e)
    {
        var ok = EstimateCorrection();
        Redraw(autoScale: false);
        if (!ok)
            WarnCorrectionFailed();
    }

    private void AutoPhaseButton_Click(object? sender, RoutedEventArgs e)
    {
        EstimatePhase();
        Redraw(autoScale: false);
    }

    private void ResetCorrectionButton_Click(object? sender, RoutedEventArgs e)
    {
        CorrectionInput.Value = 0;
        Redraw(autoScale: false);
    }

    /// <summary>Back to the phase the file was recorded with (no rotation).</summary>
    private void ResetPhaseButton_Click(object? sender, RoutedEventArgs e)
    {
        PhaseInput.Value = Math.Round(_filePhase, 1);
        Redraw(autoScale: false);
    }

    private void PhaseMinus90Button_Click(object? sender, RoutedEventArgs e) => RotatePhase(-90);

    private void PhasePlus90Button_Click(object? sender, RoutedEventArgs e) => RotatePhase(90);

    /// <summary>Adds <paramref name="deg"/> to the phase, wrapped to (−180, 180] like the Auto estimate.</summary>
    private void RotatePhase(double deg)
    {
        PhaseInput.Value = Math.Round(PhaseCorrection.Normalize(PhaseInput.Value + deg), 1);
        Redraw(autoScale: false);
    }

    private void AutoFieldBackgroundButton_Click(object? sender, RoutedEventArgs e)
    {
        EstimateFieldBackground();
        Redraw(autoScale: false);
    }

    private void ResetFieldBackgroundButton_Click(object? sender, RoutedEventArgs e)
    {
        FieldInterceptInput.Value = 0;
        FieldSlopeInput.Value = 0;
        Redraw(autoScale: false);
    }

    private void ResetGridButton_Click(object? sender, RoutedEventArgs e)
    {
        GuessGrid();
        Redraw(autoScale: false);
    }

    private RecordedSignal SelectedRecorded =>
        RecordedSelector.SelectedIndex == 1 ? RecordedSignal.Absorption : RecordedSignal.Derivative;

    /// <summary>The legend names the quantity; "Offset to 0" goes back to its default for the new combination.</summary>
    private void RecordedSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_settingInputs || ApplyOffsetCheckBox is null)
            return;
        ApplyOffsetDefault();
        if (_measurement is not null)
            Redraw(autoScale: false);
    }

    /// <summary>Another form is shown; "Offset to 0" goes back to its default for it.</summary>
    private void SignalSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_settingInputs || ApplyOffsetCheckBox is null)
            return;
        ApplyOffsetDefault();
        if (_measurement is not null)
            Redraw(autoScale: false);
    }

    private void SweepSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_settingInputs && _measurement is not null)
            Redraw(autoScale: false);
    }

    private SmoothingMethod SelectedSmoothingMethod => SmoothingSelector.SelectedIndex switch
    {
        1 => SmoothingMethod.Fft,
        2 => SmoothingMethod.SavitzkyGolay,
        _ => SmoothingMethod.None,
    };

    /// <summary>Smoothing from the entries, made valid (span odd and above the order for Savitzky-Golay).</summary>
    private SmoothingSettings SmoothingFromInputs => new SmoothingSettings(
        SelectedSmoothingMethod, (int)Math.Round(SmoothingPointsInput.Value), SmoothingOrderSelector.SelectedIndex + 1).Coerce();

    /// <summary>Puts smoothing settings into the entries and shows the ones its method uses.</summary>
    private void SetSmoothingInputs(SmoothingSettings smoothing)
    {
        _settingInputs = true;
        SmoothingSelector.SelectedIndex = smoothing.Method switch
        {
            SmoothingMethod.Fft => 1,
            SmoothingMethod.SavitzkyGolay => 2,
            _ => 0,
        };
        if (smoothing.Method == SmoothingMethod.SavitzkyGolay)
            SmoothingOrderSelector.SelectedIndex = smoothing.PolynomialOrder - 1;
        _settingInputs = false;

        if (smoothing.Method != SmoothingMethod.None)
            SmoothingPointsInput.Value = smoothing.Points;
        SmoothingPointsRow.IsVisible = smoothing.Method != SmoothingMethod.None;
        SmoothingOrderRow.IsVisible = smoothing.Method == SmoothingMethod.SavitzkyGolay;
        // Savitzky-Golay spans are odd, so the wheel steps by 2.
        SmoothingPointsInput.Step = smoothing.Method == SmoothingMethod.SavitzkyGolay ? 2 : 1;
    }

    /// <summary>A newly chosen method starts with its default span (FFT 3, Savitzky-Golay 5 points).</summary>
    private void SmoothingSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Fires while the XAML is loading, before the named controls exist; ignored while set from code.
        if (_settingInputs || SmoothingSelector is null || SmoothingPointsInput is null)
            return;

        var method = SelectedSmoothingMethod;
        var defaults = SmoothingSettings.Default(method);
        SetSmoothingInputs(method == SmoothingMethod.SavitzkyGolay
            ? (defaults with { PolynomialOrder = SmoothingOrderSelector.SelectedIndex + 1 }).Coerce()
            : defaults);
        if (_measurement is not null)
            Redraw(autoScale: false);
    }

    /// <summary>A higher order may need a wider window.</summary>
    private void SmoothingOrderSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_settingInputs || SmoothingSelector is null || SmoothingPointsInput is null)
            return;

        SetSmoothingInputs(SmoothingFromInputs);
        if (_measurement is not null)
            Redraw(autoScale: false);
    }

    /// <summary>A typed span is made valid (e.g. 4 → 5 for Savitzky-Golay) before it is used.</summary>
    private void SmoothingPointsInput_ValueCommitted(object? sender, RoutedEventArgs e)
    {
        SmoothingPointsInput.Value = SmoothingFromInputs.Points;
        if (_measurement is not null)
            _throttle.Request();
    }

    /// <summary>An entry was changed by the user (Enter, leaving the field or a wheel step).</summary>
    private void Input_ValueCommitted(object? sender, RoutedEventArgs e)
    {
        if (_measurement is not null)
            _throttle.Request();
    }

    private void GuessGrid()
    {
        if (_sweeps is null)
            return;
        var step = GridStepInput.Value > 0 ? GridStepInput.Value : CurrentSettings.GridStepOe;
        var grid = XSignalProcessing.GuessGrid(_sweeps, Correction, Selection, step);
        GridFromInput.Value = grid.From;
        GridToInput.Value = grid.To;
    }

    private void EstimateFieldBackground()
    {
        if (_sweeps is null)
            return;
        var background = XSignalProcessing.AutoFieldBackground(_sweeps, Correction, PhaseDelta, ProcessingSettings);
        FieldInterceptInput.Value = background.Intercept;
        FieldSlopeInput.Value = background.Slope;
    }

    /// <summary>
    /// Rounded to 0.1 Oe, the precision the entry shows. False if the estimate failed (best match at an end of the
    /// search window); the correction is then 0, since the end of the window is no better a guess.
    /// </summary>
    private bool EstimateCorrection()
    {
        if (_sweeps is null)
            return true;
        var estimate = SweepLagCorrection.Estimate(_sweeps, CurrentSettings.LagSearchMinS, CurrentSettings.LagSearchMaxS);
        CorrectionInput.Value = estimate.AtSearchLimit ? 0 : Math.Round(estimate.FieldCorrection, 1);
        return !estimate.AtSearchLimit;
    }

    /// <summary>Shown after the plot is drawn, so the user can judge the curves behind the message.</summary>
    private async void WarnCorrectionFailed()
    {
        var s = CurrentSettings;
        await ConfirmDialog.Inform(this, "Sweep correction",
            "Automatic sweep correction alignment failed: the up and down sweeps match best outside the search " +
            $"window ({s.LagSearchMinS.ToString(CultureInfo.InvariantCulture)} to " +
            $"{s.LagSearchMaxS.ToString(CultureInfo.InvariantCulture)} s, lag_search_min_s and lag_search_max_s " +
            "in fmr-fit.ini). The correction is set to 0. Use your own judgement.");
    }

    private void EstimatePhase()
    {
        if (_measurement is null)
            return;
        // Uses the current sweep correction, so estimate that first.
        var delta = _sweeps is not null
            ? PhaseCorrection.EstimateAtResonance(_sweeps, Correction)
            : PhaseCorrection.EstimateMinYVariance(_measurement.Field, _measurement.X, _measurement.Y);
        PhaseInput.Value = Math.Round(PhaseCorrection.Normalize(_filePhase + delta), 1);
    }

    /// <summary>Rebuilds the plot from the current entries (setting an entry from code never redraws by itself).</summary>
    private void Redraw(bool autoScale)
    {
        _throttle.Redrawing();
        var plot = Plot.Plot;
        var limits = plot.Axes.GetLimits();
        plot.Clear();
        var view = SelectedView;
        var appliedOffset = 0.0;

        if (_measurement is not null)
        {
            var phase = PhaseDelta;
            if (_sweeps is null)
            {
                var (x, y) = PhaseCorrection.Rotate(_measurement.X, _measurement.Y, phase);
                AddSeries(_measurement.Field, x, "X", Palette.GetColor(0), dashed: false);
                AddSeries(_measurement.Field, y, "Y", Palette.GetColor(1), dashed: false);
            }
            else
            {
                var (upField, downField) = SweepLagCorrection.ApplyFieldCorrection(_sweeps, Correction);
                var (upX, upY) = PhaseCorrection.Rotate(_sweeps.Up.X, _sweeps.Up.Y, phase);
                var (downX, downY) = PhaseCorrection.Rotate(_sweeps.Down.X, _sweeps.Down.Y, phase);
                AddSeries(upField, upX, "X up", Palette.GetColor(0), dashed: false);
                AddSeries(downField, downX, "X down", Palette.GetColor(0), dashed: true);
                AddSeries(upField, upY, "Y up", Palette.GetColor(1), dashed: false);
                AddSeries(downField, downY, "Y down", Palette.GetColor(1), dashed: true);

                var (processedField, processedX) = XSignalProcessing.Process(_sweeps, Correction, phase, ProcessingSettings);
                if (processedField.Length > 0)
                {
                    var label = view switch
                    {
                        SignalView.Integral => "∫X dH processed",
                        SignalView.Derivative => "dX/dH processed",
                        _ => "X processed",
                    };
                    label += $" = {MeasuredQuantity.Name(MeasuredQuantity.Order(SelectedRecorded, view))}";
                    if (PowerFactor != 1)
                        label += $", norm. to {PowerNormalization.ReferenceDbm:0} dBm";
                    var settings = ProcessingSettings;
                    var shown = SignalCalculus.Displayed(processedField, processedX, view, settings.ApplyOffset,
                        settings.Offset);
                    appliedOffset = settings.ApplyOffset ? settings.Offset : 0;
                    if (appliedOffset != 0)
                        label += ", offset";
                    var line = AddSeries(processedField, shown, label, Colors.Red, dashed: false);
                    // Integral and derivative have other units than X and Y: they get the right axis.
                    if (view != SignalView.Signal)
                        line.Axes.YAxis = plot.Axes.Right;
                }
            }

            var frequency = _measurement.Metadata.FrequencyGHz is { } f
                ? f.ToString(CultureInfo.InvariantCulture) + " GHz"
                : "unknown frequency";
            plot.Title($"{_measurement.Metadata.SampleName ?? _fileName}, {frequency}");
            plot.ShowLegend();
        }

        SetAxisLabels(view);
        if (autoScale)
        {
            plot.Axes.AutoScale();
        }
        else
        {
            plot.Axes.SetLimits(limits);
            // The right axis follows its curve when it is first shown or switches between integral and derivative.
            if (view != SignalView.Signal && view != _rightAxisView)
                plot.Axes.AutoScaleY(plot.Axes.Right);
            // A changed offset moves the processed curve: follow it on its axis, so it doesn't leave the view.
            else if (appliedOffset != _shownOffset)
                plot.Axes.AutoScaleY(view == SignalView.Signal ? plot.Axes.Left : plot.Axes.Right);
        }
        _rightAxisView = view;
        _shownOffset = appliedOffset;
        Plot.Refresh();
    }

    /// <summary>The view shown on the right axis at the last redraw (Signal: right axis unused).</summary>
    private SignalView _rightAxisView;

    /// <summary>Offset added to the processed curve at the last redraw (0 when not applied).</summary>
    private double _shownOffset;

    private ScottPlot.Plottables.Scatter AddSeries(double[] xs, double[] ys, string label, Color color, bool dashed)
    {
        var line = Plot.Plot.Add.ScatterLine(xs, ys, color);
        line.LegendText = label;
        if (dashed)
            line.LinePattern = LinePattern.Dashed;
        return line;
    }

    private void SetAxisLabels(SignalView view = SignalView.Signal)
    {
        Plot.Plot.XLabel("Field (Oe)");
        Plot.Plot.YLabel("Signal (V)");
        Plot.Plot.Axes.Right.Label.Text = view switch
        {
            SignalView.Integral => "Integral (V·Oe)",
            SignalView.Derivative => "Derivative (V/Oe)",
            _ => "",
        };
        var unit = view switch
        {
            SignalView.Integral => "V·Oe",
            SignalView.Derivative => "V/Oe",
            _ => "V",
        };
        OffsetInput.Description = $"Offset added to the shown form ({unit}) while \"Offset to 0\" is on. A sets it so the minimum is 0.";
    }
}
