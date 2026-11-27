using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Brush = Avalonia.Media.Brush;
using HAlign = Avalonia.Layout.HorizontalAlignment;
using IBrush = Avalonia.Media.IBrush;
using VAlign = Avalonia.Layout.VerticalAlignment;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FmrFit.Core.Fitting;
using FmrFit.Core.IO;
using FmrFit.Core.Processing;
using ScottPlot;

namespace FmrFit.App;

/// <summary>
/// Fits a sum of peaks to one work file. The main pane shows the data, each peak and their sum; the pane below
/// shares the field axis and shows the residual (data − fit) with its standard deviation. The selected peak can be
/// adjusted by hand: value and limit entries and sliders in the side panel, and a position slider between the panes.
/// </summary>
public partial class DataFitting : Window
{
    /// <summary>Same plot-area margins in both panes, so their field axes line up.</summary>
    private static readonly PixelPadding PlotPadding = new(left: 90, right: 20, bottom: 50, top: 15);

    private static readonly IPalette Palette = new ScottPlot.Palettes.Category10();

    /// <summary>Space above and below the position slider, between the two panes.</summary>
    private const double XcSliderGap = 5;

    private static readonly LinePattern DashDot = new([10, 4, 2, 4], 0, "Dash-dot");

    /// <summary>Minimum (blue) and maximum (red) of a position: entry borders; the slider shading uses the same hues.</summary>
    private static readonly IBrush MinimumBrush = Brush.Parse("#3D8BFD");
    private static readonly IBrush MaximumBrush = Brush.Parse("#E5534B");

    private List<Peak> _peaks = [];
    private ObservableCollection<PeakListItem> _items = [];
    /// <summary>Statistics of the peaks over the fit range; null without peaks.</summary>
    private FitResult? _result;

    /// <summary>Each peak evaluated over the whole spectrum, kept so an edit re-evaluates only the changed peak.</summary>
    private PeakCurves _curves = PeakCurves.Empty;

    /// <summary>Rate limit of the redraws after manual edits (slider drags, wheel steps).</summary>
    private readonly RedrawThrottle _throttle;

    /// <summary>Field range used for fitting and statistics; the whole spectrum by default.</summary>
    private FieldRange _range;

    /// <summary>Set when the graph should be rescaled to <see cref="_range"/> on the next redraw.</summary>
    private bool _rescale = true;

    private readonly NumberBox _rangeMinBox;
    private readonly NumberBox _rangeMaxBox;

    /// <summary>Set while code changes inputs, so their change events don't edit the peak again.</summary>
    private bool _settingInputs;

    /// <summary>Controls of the selected peak's parameters, indexed like its parameters.</summary>
    private List<ParameterControls> _controls = [];

    /// <summary>
    /// Entries and slider of one parameter: a limit for widths, a minimum and maximum for positions and amplitudes.
    /// The slider spans exactly the allowed range (the position's slider is the one under the graph).
    /// </summary>
    private sealed record ParameterControls(
        int Index, ParameterKind Kind, NumberBox Value, Slider? Slider, NumberBox? Limit, NumberBox? Min, NumberBox? Max,
        CheckBox Fixed, CheckBox? KeepHeight)
    {
        public bool Owns(NumberBox box) =>
            ReferenceEquals(Value, box) || ReferenceEquals(Limit, box) || ReferenceEquals(Min, box) || ReferenceEquals(Max, box);
    }

    private enum LimitEnd { Limit, Minimum, Maximum }

    /// <summary>Shading behind the position slider: blue below the minimum, red above the maximum.</summary>
    private sealed record RangeBands(Canvas Canvas, Avalonia.Controls.Shapes.Rectangle Below, Avalonia.Controls.Shapes.Rectangle Above);

    private static bool HasMinMax(ParameterKind kind) => kind != ParameterKind.Width;

    /// <summary>Data area of the main pane in layout units: left edge and distance of the right edge from the right.</summary>
    private double _dataLeft, _dataRight;

    public DataFitting()
    {
        InitializeComponent();

        ShapeSelector.ItemsSource = PeakShapes.All.Select(s => s.Name).ToList();
        ShapeSelector.SelectedIndex = 0;
        PeakList.ItemsSource = _items;

        const string rangeTip = " Empty = data limit. Kept inside the data and below/above the other end.";
        _rangeMinBox = NewNumberBox("Lower end of the fit range (blue)." + rangeTip, MinimumBrush);
        _rangeMaxBox = NewNumberBox("Upper end of the fit range (red)." + rangeTip, MaximumBrush);
        _rangeMinBox.EmptyValue = () => DataRange.Min;
        _rangeMaxBox.EmptyValue = () => DataRange.Max;
        Grid.SetColumn(_rangeMinBox, 1);
        Grid.SetColumn(_rangeMaxBox, 2);
        RangeRow.Children.Add(_rangeMinBox);
        RangeRow.Children.Add(_rangeMaxBox);
        _rangeMinBox.ValueCommitted += (_, _) => CommitRange(isMin: true);
        _rangeMaxBox.ValueCommitted += (_, _) => CommitRange(isMin: false);

        MainPlot.Plot.Layout.Fixed(PlotPadding);
        ResidualPlot.Plot.Layout.Fixed(PlotPadding);
        MainPlot.Plot.Axes.Link(ResidualPlot, x: true, y: false);
        ResidualPlot.Plot.Axes.Link(MainPlot, x: true, y: false);

        // Rendering may happen off the UI thread; follow the field axis with the position slider on the UI thread.
        MainPlot.Plot.RenderManager.RenderFinished += (_, _) => Dispatcher.UIThread.Post(FollowFieldAxis);

        _throttle = new RedrawThrottle(Redraw, () => CurrentSettings.MaxRedrawsPerSecond);
        Closed += (_, _) =>
        {
            _throttle.Stop();
            Settings?.CaptureWindowSize(this);
        };

        Opened += (_, _) =>
        {
            if (WorkFiles.Count > 0)
                LoadWorkFile(Math.Clamp(StartIndex, 0, WorkFiles.Count - 1));
        };
    }

    /// <summary>The readable work files of the main window, in its order; Next steps through them.</summary>
    public IReadOnlyList<(string Path, WorkFile File)> WorkFiles { get; init; } = [];

    /// <summary>Index in <see cref="WorkFiles"/> of the work file to fit first.</summary>
    public int StartIndex { get; init; }

    /// <summary>User settings: redraw rate, fit iterations, default limits of new peaks, the window size.</summary>
    public SettingsStore? Settings { get; init; }

    private AppSettings CurrentSettings => Settings?.Current ?? AppSettings.Default;

    /// <summary>Path of the work file shown last, so the main window can select it.</summary>
    public string? LastPath => WorkFilePath;

    /// <summary>Index in <see cref="WorkFiles"/> of the work file being fitted, or −1 before loading.</summary>
    private int _current = -1;

    /// <summary>True if the peaks changed since they were loaded or saved.</summary>
    private bool _dirty;

    /// <summary>Message about loading or saving, shown under the result; null if none.</summary>
    private string? _status;

    private WorkFile? WorkFile => _current >= 0 ? WorkFiles[_current].File : null;

    private string? WorkFilePath => _current >= 0 ? WorkFiles[_current].Path : null;

    private string? FitFilePath => WorkFilePath is { } path ? ProjectFolder.FitFilePath(path) : null;

    private double[] Field => WorkFile?.FieldOe ?? [];
    /// <summary>
    /// The fitted data: the work file's displayed column (signal, integral or derivative) with its offset if applied,
    /// set when the file is loaded.
    /// </summary>
    private double[] Signal => _data;
    private double[] _data = [];

    /// <summary>Which column <see cref="Signal"/> holds.</summary>
    private SignalView DataView => WorkFile is { } file ? SignalCalculus.DisplayedView(file) : SignalView.Signal;

    /// <summary>Power of Oe in the data unit (mV·Oe^power), for the units of the parameters and statistics.</summary>
    private int DataPower => SignalCalculus.FieldPower(DataView);

    private string DataLabel => DataView switch
    {
        SignalView.Integral => "Integral",
        SignalView.Derivative => "Derivative",
        _ => "Signal",
    } + $" ({ParameterUnits.Data(DataPower)})";

    private int SelectedIndex => PeakList.SelectedIndex >= 0 && PeakList.SelectedIndex < _peaks.Count ? PeakList.SelectedIndex : -1;

    private Peak? SelectedPeak => SelectedIndex >= 0 ? _peaks[SelectedIndex] : null;

    private void AddPeakButton_Click(object? sender, RoutedEventArgs e)
    {
        if (WorkFile is null || Field.Length == 0)
            return;
        var (field, signal) = InRange();
        if (field.Length == 0)
            return;
        var shape = PeakShapes.All[Math.Max(ShapeSelector.SelectedIndex, 0)];
        _peaks.Add(PeakFitter.GuessPeak(shape, field, signal, _peaks, CurrentSettings.DefaultLimitFactor));
        MarkDirty();
        Update(Statistics(), select: _peaks.Count - 1);
    }

    private void FitButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_peaks.Count == 0)
            return;
        var (field, signal) = InRange();
        var result = PeakFitter.Fit(field, signal, _peaks, CurrentSettings.MaxIterations);
        _peaks = result.Peaks.ToList();
        MarkDirty();
        Update(result, select: PeakList.SelectedIndex);
    }

    private void FitSelectedButton_Click(object? sender, RoutedEventArgs e)
    {
        var index = SelectedIndex;
        if (index < 0)
            return;
        var (field, signal) = InRange();
        var result = PeakFitter.FitPeak(field, signal, _peaks, index, CurrentSettings.MaxIterations);
        _peaks = result.Peaks.ToList();
        MarkDirty();
        Update(result, select: index);
    }

    private void RemovePeakButton_Click(object? sender, RoutedEventArgs e)
    {
        var index = SelectedIndex;
        if (index < 0)
            return;
        _peaks.RemoveAt(index);
        MarkDirty();
        Update(Statistics(), select: Math.Min(index, _peaks.Count - 1));
    }

    // ---- Fit range ----

    private FieldRange DataRange => FieldRange.Of(Field) ?? new FieldRange(0, 1);

    /// <summary>The data points inside the fit range.</summary>
    private (double[] Field, double[] Signal) InRange() => _range.Slice(Field, Signal);

    /// <summary>The current peaks compared with the data inside the fit range.</summary>
    private FitResult Statistics() => Curves().Compare(Signal, _range);

    /// <summary>The current peaks' curves over the whole spectrum; only peaks whose values changed are evaluated.</summary>
    private PeakCurves Curves() => _curves = _curves.Update(Field, _peaks);

    /// <summary>
    /// Applies an entered range end: clamped into the data, empty = the data limit. A range that is empty or has fewer
    /// than two points is refused and the entry shows the current value again.
    /// </summary>
    private void CommitRange(bool isMin)
    {
        if (WorkFile is null)
            return;
        var data = DataRange;
        var end = (isMin ? _rangeMinBox : _rangeMaxBox).Value;
        if ((isMin ? FieldRange.Create(end, _range.Max, data) : FieldRange.Create(_range.Min, end, data)) is { } range
            && range != _range
            && range.Slice(Field, Signal).Field.Length >= 2)
        {
            _range = range;
            MarkDirty();
            _result = _peaks.Count > 0 ? Statistics() : null;
            _rescale = true;
            Redraw();
        }
        SyncRangeBoxes();
    }

    private void SyncRangeBoxes()
    {
        _rangeMinBox.Value = _range.Min;
        _rangeMaxBox.Value = _range.Max;
    }

    // ---- Saving and loading ----

    /// <summary>
    /// Shows work file <paramref name="index"/> of <see cref="WorkFiles"/> with the peaks of its saved fit, if any.
    /// A fit file that cannot be read leaves no peaks and a message.
    /// </summary>
    private void LoadWorkFile(int index, bool keepView = false)
    {
        _current = index;
        _data = WorkFile is { } workFile ? SignalCalculus.DisplayedColumn(workFile) : [];
        if (!keepView)
            _rescale = true;
        _status = null;

        var peaks = new List<Peak>();
        var range = DataRange;
        (int Iterations, string ExitReason)? fitted = null;
        if (FitFilePath is { } fitPath && File.Exists(fitPath))
        {
            try
            {
                var file = FitFileReader.Read(fitPath);
                peaks = FitFileConversion.ToPeaks(file);
                range = FitFileConversion.RangeOf(file, DataRange);
                if (file.Iterations is { } iterations)
                    fitted = (iterations, file.ExitReason ?? "");
                _status = $"Loaded {Path.GetFileName(fitPath)}.";
                if (FitFileConversion.DataOf(file) is var fittedData && fittedData != DataView)
                    _status += $" It was fitted on the {SignalViewText.ToText(fittedData)}, but the work file now " +
                               $"displays the {SignalViewText.ToText(DataView)}: fit again.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                _status = $"Could not load {Path.GetFileName(fitPath)}: {ex.Message}";
            }
        }
        else if (WorkFile is { } current)
        {
            // Not fitted yet: offer the line shape of the quantity shown, e.g. Lorentzian derivative for dP/dH,
            // Lorentzian for ~P or integrated dP/dH.
            var shape = PeakShapes.DefaultFor(MeasuredQuantity.Order(current));
            ShapeSelector.SelectedIndex = PeakShapes.All.ToList().IndexOf(shape);
        }

        _peaks = peaks;
        _range = range;
        SyncRangeBoxes();
        _dirty = false;
        var result = Statistics();
        if (fitted is { } f)
            result = result with { Iterations = f.Iterations, ExitReason = f.ExitReason };
        Update(result, select: _peaks.Count > 0 ? 0 : -1);
        UpdateReloadButton();
    }

    /// <summary>Writes the current peaks and statistics to the fit file; false (with a message) if that fails.</summary>
    private bool SaveFit()
    {
        if (WorkFilePath is not { } path || FitFilePath is not { } fitPath)
            return false;
        var result = _result ?? Statistics();
        // The whole spectrum is stored as "no range".
        var file = FitFileConversion.ToFitFile(Path.GetFileName(path), _peaks, result, DateTimeOffset.Now,
            _range == DataRange ? null : _range, DataView);
        try
        {
            FitFileWriter.Write(fitPath, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status = $"Could not save {Path.GetFileName(fitPath)}: {ex.Message}";
            ShowResult();
            return false;
        }
        _dirty = false;
        _status = $"Saved {Path.GetFileName(fitPath)}.";
        ShowResult();
        UpdateReloadButton();
        return true;
    }

    /// <summary>Notes a change to the peaks: the saved fit no longer matches, so any "saved"/"loaded" message goes.</summary>
    private void MarkDirty()
    {
        _dirty = true;
        _status = null;
    }

    private void UpdateReloadButton() => ReloadButton.IsEnabled = FitFilePath is { } p && File.Exists(p);

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (SaveFit())
            Close();
    }

    private void NextButton_Click(object? sender, RoutedEventArgs e)
    {
        if (WorkFiles.Count > 0 && SaveFit())
            LoadWorkFile((_current + 1) % WorkFiles.Count);
    }

    /// <summary>Closes without saving; <see cref="OnClosing"/> asks first if there are unsaved changes.</summary>
    private void DiscardButton_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Set once the user agreed to discard unsaved changes, so the repeated close goes through.</summary>
    private bool _closeConfirmed;

    /// <summary>Set while the discard question is open, so a second close request doesn't ask again.</summary>
    private bool _askingToClose;

    /// <summary>
    /// Closing by Discard, the window's close button or Alt+F4 with unsaved changes: the close is cancelled and the user
    /// is asked, as for Reload; on confirmation the window closes. Closing because the application shuts down is not held.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || !_dirty || _closeConfirmed || e.CloseReason != WindowCloseReason.WindowClosing)
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
            var name = Path.GetFileName(WorkFilePath) ?? "this work file";
            if (!await ConfirmDialog.Ask(this, "Discard changes",
                    $"Discard the unsaved changes to the fit of \"{name}\" and close?", "Discard"))
                return;
            _closeConfirmed = true;
            Close();
        }
        finally
        {
            _askingToClose = false;
        }
    }

    private async void ReloadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (FitFilePath is not { } fitPath || !File.Exists(fitPath))
            return;
        if (_dirty && !await ConfirmDialog.Ask(this, "Reload fit",
                $"Discard the changes and reload the peaks from \"{Path.GetFileName(fitPath)}\"?", "Reload"))
            return;
        LoadWorkFile(_current, keepView: true);
    }

    private void PeakList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionButtons();
        BuildAdjustRows();
        Redraw();
    }

    private void UpdateSelectionButtons()
    {
        RemovePeakButton.IsEnabled = SelectedIndex >= 0;
        FitSelectedButton.IsEnabled = SelectedIndex >= 0;
    }

    private void Update(FitResult result, int select)
    {
        _result = _peaks.Count > 0 ? result : null;
        _items = new ObservableCollection<PeakListItem>(_peaks.Select((p, i) => new PeakListItem(DescribePeak(i, p, DataPower))));
        PeakList.ItemsSource = _items;
        PeakList.SelectedIndex = select;
        FitButton.IsEnabled = RefreshButton.IsEnabled = _peaks.Count > 0;
        UpdateSelectionButtons();
        BuildAdjustRows();
        Redraw();
    }

    // ---- Manual adjustment of the selected peak ----

    /// <summary>Builds value/limit entries and sliders for the selected peak's parameters.</summary>
    private void BuildAdjustRows()
    {
        AdjustPanel.Children.Clear();
        _controls = [];
        var peak = SelectedPeak;
        AdjustHint.Text = _peaks.Count == 0
            ? "No peaks yet. Choose a function and press Add peak."
            : peak is null ? "Select a peak to adjust it." : "";
        AdjustHint.IsVisible = AdjustHint.Text.Length > 0;
        XcSlider.IsEnabled = peak is not null && PositionIndex(peak) >= 0;
        FixAllButton.IsEnabled = peak is not null;
        if (peak is null)
        {
            FixAllButton.Content = "Fix all";
            UpdateXcShading();
            return;
        }

        var shape = peak.Shape;
        for (var k = 0; k < shape.ParameterNames.Count; k++)
        {
            var kind = shape.ParameterKinds[k];
            var index = k;

            // Columns: name | minimum | value | maximum or limit | fixed; values line up across rows.
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,96,96,96,Auto"), ColumnSpacing = 6, Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(new TextBlock
            {
                Text = $"{shape.ParameterNames[k]} ({ParameterUnits.OfData(kind, DataPower)})",
                VerticalAlignment = VAlign.Center,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
            });

            var name = shape.ParameterNames[k];
            var value = NewNumberBox(kind == ParameterKind.Position
                ? $"Value of {name}. Also set by the slider under the graph."
                : $"Value of {name}. Also set by the slider below.");
            Grid.SetColumn(value, 2);
            row.Children.Add(value);
            value.ValueCommitted += (_, _) => CommitValue(index, value);

            NumberBox? limit = null, min = null, max = null;
            CheckBox? keepHeight = null;
            Slider? slider = null;
            if (HasMinMax(kind))
            {
                min = NewNumberBox($"Minimum of {name} (blue): {name} and the fit stay at or above it; never above the maximum.",
                    MinimumBrush);
                max = NewNumberBox($"Maximum of {name} (red): {name} and the fit stay at or below it; never below the minimum.",
                    MaximumBrush);
                Grid.SetColumn(min, 1);
                Grid.SetColumn(max, 3);
                row.Children.Add(min);
                row.Children.Add(max);
                var (minBox, maxBox) = (min, max);
                min.ValueCommitted += (_, _) => CommitLimit(index, minBox, LimitEnd.Minimum);
                max.ValueCommitted += (_, _) => CommitLimit(index, maxBox, LimitEnd.Maximum);
            }
            else
            {
                limit = NewNumberBox($"Limit: largest {name}, from {PeakLimits.MinWidth.ToString(CultureInfo.InvariantCulture)} Oe (the hard minimum) to this value. Also limits the slider and the fit.");
                Grid.SetColumn(limit, 3);
                row.Children.Add(limit);
                var limitBox = limit;
                limit.ValueCommitted += (_, _) => CommitLimit(index, limitBox, LimitEnd.Limit);

                if (PeakScaling.Applies(shape))
                {
                    var scaled = string.Join("; ", shape.ParameterNames
                        .Select((n, j) => (Name: n, Power: PeakScaling.WidthPower(shape.ParameterKinds[j])))
                        .Where(a => a.Power > 0)
                        .GroupBy(a => a.Power)
                        .Select(g => $"{string.Join(", ", g.Select(a => a.Name))} are scaled with {name}{(g.Key == 2 ? "²" : "")}"));
                    keepHeight = new CheckBox { MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VAlign.Center, HorizontalAlignment = HAlign.Right };
                    ToolTip.SetTip(keepHeight, $"Keep the peak height when {name} is changed by hand: {scaled} "
                        + "and their minimum or maximum widened if needed. The fit does not use this.");
                    Grid.SetColumn(keepHeight, 1);
                    row.Children.Add(keepHeight);
                    var keepBox = keepHeight;
                    keepHeight.IsCheckedChanged += (_, _) => KeepHeightBox_Changed(keepBox);
                }
            }

            if (kind != ParameterKind.Position)
            {
                // The position has its slider under the graph; the others get one here.
                slider = new Slider { MinHeight = 0, Margin = new Thickness(0, -6, 0, -6) };
                slider.ValueChanged += (_, e) => Slider_ValueChanged(index, e.NewValue);
            }

            var fix = new CheckBox { MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VAlign.Center };
            ToolTip.SetTip(fix, $"Fix {name}: Fit and Fit selected leave it unchanged. Manual changes still apply.");
            Grid.SetColumn(fix, 4);
            row.Children.Add(fix);
            fix.IsCheckedChanged += (_, _) => FixedBox_Changed(index, fix);

            AdjustPanel.Children.Add(row);
            if (slider is not null)
                AdjustPanel.Children.Add(slider);
            _controls.Add(new ParameterControls(index, kind, value, slider, limit, min, max, fix, keepHeight));
        }
        SyncControls();
    }

    private static NumberBox NewNumberBox(string description, IBrush? border = null)
    {
        var box = new NumberBox { Padding = new Thickness(4, 5), Description = description };
        if (border is not null)
        {
            box.BorderBrush = border;
            // Keep the colour when the theme restyles the border on hover and focus.
            box.Resources["TextControlBorderBrushPointerOver"] = border;
            box.Resources["TextControlBorderBrushFocused"] = border;
        }
        return box;
    }

    /// <summary>Applies an entered value (Enter, leaving the field or a wheel step); clamped values show afterwards.</summary>
    private void CommitValue(int index, NumberBox box)
    {
        // The box may belong to rows already replaced (focus left it because another peak was selected).
        if (!_controls.Any(c => c.Owns(box)) || SelectedPeak is null)
            return;
        SetParameter(index, box.Value);
        SyncControls();
    }

    /// <summary>
    /// Applies an entered limit, minimum or maximum. A minimum above the maximum (or a maximum below the minimum) is
    /// set equal to it; a limit that is not positive is refused.
    /// </summary>
    private void CommitLimit(int index, NumberBox box, LimitEnd end)
    {
        // The box may belong to rows already replaced (focus left it because another peak was selected).
        if (!_controls.Any(c => c.Owns(box)) || SelectedPeak is not { } peak)
            return;
        var typed = box.Value;
        var changed = end switch
        {
            LimitEnd.Minimum => peak.WithMinimum(index, typed),
            LimitEnd.Maximum => peak.WithMaximum(index, typed),
            _ => typed > 0 ? peak.WithLimit(index, typed) : null,
        };
        if (changed is not null)
        {
            _peaks[SelectedIndex] = changed;
            ApplyEdit();
        }
        SyncControls();
    }

    private void KeepHeightBox_Changed(CheckBox box)
    {
        if (_settingInputs || SelectedPeak is not { } peak)
            return;
        _peaks[SelectedIndex] = peak with { KeepHeight = box.IsChecked == true };
        MarkDirty();
        ShowResult();
    }

    private void FixedBox_Changed(int index, CheckBox box)
    {
        if (_settingInputs || SelectedPeak is not { } peak)
            return;
        SetFixing(peak.WithFixed(index, box.IsChecked == true));
    }

    /// <summary>Fix all parameters of the selected peak, or free them all if all are fixed.</summary>
    private void FixAllButton_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedPeak is { } peak)
            SetFixing(peak.WithAllFixed(!peak.AllFixed));
    }

    /// <summary>
    /// Stores the selected peak with changed fixing. Values are unchanged, so the last fit's iteration count is kept;
    /// σ is recomputed because fixed parameters don't count as fitted.
    /// </summary>
    private void SetFixing(Peak peak)
    {
        _peaks[SelectedIndex] = peak;
        _result = StatisticsKeepingIterations();
        _items[SelectedIndex].Text = DescribePeak(SelectedIndex, peak, DataPower);
        MarkDirty();
        ShowResult();
        SyncControls();
    }

    private void Slider_ValueChanged(int index, double value)
    {
        if (!_settingInputs)
            SetParameter(index, value);
    }

    private void XcSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_settingInputs && SelectedPeak is { } peak && PositionIndex(peak) is var k and >= 0)
            SetParameter(k, e.NewValue);
    }

    /// <summary>A manual change of one parameter; a width change scales the amplitudes if the peak keeps its height.</summary>
    private void SetParameter(int index, double value)
    {
        if (SelectedPeak is not { } peak)
            return;
        var keepHeight = peak.KeepHeight && peak.Shape.ParameterKinds[index] == ParameterKind.Width && PeakScaling.Applies(peak.Shape);
        _peaks[SelectedIndex] = keepHeight ? peak.WithWidthKeepingHeight(index, value) : peak.WithParameter(index, value);
        ApplyEdit();
    }

    /// <summary>
    /// After a manual change: compare with the data, refresh the list text and other controls, redraw (rate-limited,
    /// as a slider drag repeats this many times a second).
    /// </summary>
    private void ApplyEdit()
    {
        _result = Statistics();
        MarkDirty();
        RefreshView(limitRate: true);
    }

    /// <summary>
    /// Runs the after-a-change routine on demand, with every peak evaluated again from scratch (the stored curves are
    /// replaced). The peaks are unchanged, so they are not marked unsaved and the last fit's iteration count is kept.
    /// </summary>
    private void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_peaks.Count == 0)
            return;
        _curves = PeakCurves.Empty;
        _result = StatisticsKeepingIterations();
        RefreshView();
    }

    /// <summary>
    /// Shows the current peaks: the selected peak's list text, its entries and sliders, and the graphs (with
    /// <paramref name="limitRate"/>, through <see cref="_throttle"/>).
    /// </summary>
    private void RefreshView(bool limitRate = false)
    {
        if (SelectedIndex >= 0 && SelectedIndex < _items.Count)
            _items[SelectedIndex].Text = DescribePeak(SelectedIndex, _peaks[SelectedIndex], DataPower);
        SyncControls();
        if (limitRate)
            _throttle.Request();
        else
            Redraw();
    }

    /// <summary><see cref="Statistics"/> with the last fit's iteration count, for changes that leave the values as they are.</summary>
    private FitResult StatisticsKeepingIterations()
    {
        var result = Statistics();
        return _result is { Iterations: > 0 } last
            ? result with { Iterations = last.Iterations, ExitReason = last.ExitReason }
            : result;
    }

    /// <summary>
    /// Shows the selected peak's values in its entries and sliders (an entry the user is typing in keeps its text).
    /// </summary>
    private void SyncControls()
    {
        if (SelectedPeak is not { } peak)
            return;
        _settingInputs = true;
        try
        {
            foreach (var c in _controls)
            {
                var value = c.Kind == ParameterKind.Width ? Math.Abs(peak.Parameters[c.Index]) : peak.Parameters[c.Index];
                var range = peak.Range(c.Index);
                SetText(c.Value, value);
                SetText(c.Limit, range.Max);
                SetText(c.Min, range.Min);
                SetText(c.Max, range.Max);
                c.Fixed.IsChecked = peak.IsFixed(c.Index);
                if (c.KeepHeight is not null)
                    c.KeepHeight.IsChecked = peak.KeepHeight;
                if (c.Slider is not null)
                {
                    c.Slider.Minimum = range.Min;
                    c.Slider.Maximum = range.Max;
                    c.Slider.SmallChange = (range.Max - range.Min) / 1000;
                    c.Slider.LargeChange = (range.Max - range.Min) / 20;
                    c.Slider.Value = value;
                }
            }
            if (PositionIndex(peak) is var k and >= 0)
                XcSlider.Value = peak.Parameters[k];
            FixAllButton.Content = peak.AllFixed ? "Unfix all" : "Fix all";
        }
        finally
        {
            _settingInputs = false;
        }
        UpdateXcShading();

        static void SetText(NumberBox? box, double value)
        {
            if (box is not null)
                box.Value = value;
        }
    }

    /// <summary>
    /// Keeps the position slider in step with the field axis: its range is the visible field range, and its track
    /// is placed so that the thumb centre sits at the field it shows.
    /// </summary>
    private void FollowFieldAxis()
    {
        var limits = MainPlot.Plot.Axes.GetLimits();
        var (min, max) = (Math.Min(limits.Left, limits.Right), Math.Max(limits.Left, limits.Right));
        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min)
            return;

        _settingInputs = true;
        try
        {
            XcSlider.Minimum = min;
            XcSlider.Maximum = max;
            XcSlider.SmallChange = (max - min) / 1000;
            XcSlider.LargeChange = (max - min) / 20;
            if (SelectedPeak is { } peak && PositionIndex(peak) is var k and >= 0)
                XcSlider.Value = peak.Parameters[k];
        }
        finally
        {
            _settingInputs = false;
        }

        AlignXcSlider();
        UpdateXcShading();
    }

    /// <summary>Sets the slider's margins so its thumb centre runs from the data area's left edge to its right edge.</summary>
    private void AlignXcSlider()
    {
        var render = MainPlot.Plot.LastRender;
        var figureWidth = render.FigureRect.Width;
        if (figureWidth <= 0 || MainPlot.Bounds.Width <= 0)
            return;
        // Plot pixels → layout units.
        var scale = MainPlot.Bounds.Width / figureWidth;
        var dataLeft = _dataLeft = render.DataRect.Left * scale;
        var dataRight = _dataRight = (figureWidth - render.DataRect.Right) * scale;

        // Where the thumb centre can go inside the slider: the track's insets plus half the thumb.
        var track = XcSlider.GetVisualDescendants().OfType<Track>().FirstOrDefault();
        var thumb = track?.Thumb;
        var halfThumb = thumb is { Bounds.Width: > 0 } ? thumb.Bounds.Width / 2 : 10;
        var insetLeft = 0.0;
        var insetRight = 0.0;
        if (track is not null && track.TranslatePoint(new Point(0, 0), XcSlider) is { } origin)
        {
            insetLeft = origin.X;
            insetRight = Math.Max(XcSlider.Bounds.Width - origin.X - track.Bounds.Width, 0);
        }

        var margin = new Thickness(
            Math.Max(dataLeft - insetLeft - halfThumb, 0), XcSliderGap,
            Math.Max(dataRight - insetRight - halfThumb, 0), XcSliderGap);
        if (XcSlider.Margin != margin)
            XcSlider.Margin = margin;
    }

    /// <summary>
    /// Shades a slider span [<paramref name="spanMin"/>, <paramref name="spanMax"/>] outside <paramref name="range"/>:
    /// blue below its minimum, red above its maximum. <paramref name="x"/> maps a value to the canvas x of the thumb centre.
    /// </summary>
    private static void PlaceBands(RangeBands bands, Func<double, double> x, double top, double spanMin, double spanMax, ParameterRange range)
    {
        double X(double v) => x(Math.Clamp(v, spanMin, spanMax));
        Place(bands.Below, X(spanMin), X(range.Min));
        Place(bands.Above, X(range.Max), X(spanMax));

        void Place(Avalonia.Controls.Shapes.Rectangle band, double from, double to)
        {
            var visible = to - from >= 0.5;
            if (visible && (Canvas.GetLeft(band) != from || Canvas.GetTop(band) != top || band.Width != to - from))
            {
                Canvas.SetLeft(band, from);
                Canvas.SetTop(band, top);
                band.Width = to - from;
            }
            if (band.IsVisible != visible)
                band.IsVisible = visible;
        }
    }

    /// <summary>
    /// Shades the position slider where the selected peak's position may not go: blue below its minimum, red above
    /// its maximum, using the same field-to-pixel mapping as the slider thumb and the field axis.
    /// </summary>
    private void UpdateXcShading()
    {
        var bands = new RangeBands(XcShading, XcBelowMin, XcAboveMax);
        var (axisMin, axisMax) = (XcSlider.Minimum, XcSlider.Maximum);
        var width = XcShading.Bounds.Width - _dataLeft - _dataRight;
        if (SelectedPeak is not { } peak || PositionIndex(peak) is not (var k and >= 0) || axisMax <= axisMin || width <= 0)
        {
            XcBelowMin.IsVisible = XcAboveMax.IsVisible = false;
            return;
        }
        var top = (XcShading.Bounds.Height - XcBelowMin.Height) / 2;
        PlaceBands(bands, v => _dataLeft + (v - axisMin) / (axisMax - axisMin) * width, top, axisMin, axisMax, peak.Range(k));
    }

    private static int PositionIndex(Peak peak)
    {
        for (var k = 0; k < peak.Shape.ParameterKinds.Count; k++)
            if (peak.Shape.ParameterKinds[k] == ParameterKind.Position)
                return k;
        return -1;
    }

    // ---- Plotting ----

    /// <summary>
    /// Rebuilds both panes. Each pane is built while holding its <c>Plot.Sync</c>, which ScottPlot's renderer also
    /// locks: rendering may run off the UI thread and would otherwise catch a half-built plot (missing curves and
    /// legend for a frame). The two locks are never held together.
    /// </summary>
    private void Redraw()
    {
        _throttle.Redrawing();
        if (WorkFile is null)
            return;

        var name = Path.GetFileNameWithoutExtension(WorkFilePath) ?? WorkFile.Metadata.SampleName ?? "";
        var frequency = WorkFile.Metadata.FrequencyGHz is { } f ? $", {f.ToString("0.###", CultureInfo.InvariantCulture)} GHz" : "";
        var position = WorkFiles.Count > 1 ? $"   ({_current + 1} of {WorkFiles.Count})" : "";
        TitleText.Text = name + frequency + position;
        ShowResult();

        // Curves over the whole spectrum; the statistics in _result cover only the fit range.
        var curves = _peaks.Count > 0 ? Curves() : null;
        var fit = curves?.Sum;
        var dataMinusFit = curves?.Residual(Signal);
        var markers = SelectedPeak is { } selected ? selected.Shape.Markers(selected.Parameters) : null;
        var markerColor = Palette.GetColor(Math.Max(SelectedIndex, 0));

        var main = MainPlot.Plot;
        AxisLimits x;
        lock (main.Sync)
        {
            var mainLimits = main.Axes.GetLimits();
            main.Clear();
            AddOutsideRange(main);

            var data = main.Add.ScatterLine(Field, Signal, Colors.Gray);
            data.LegendText = "Data";
            for (var i = 0; i < (curves?.Curves.Count ?? 0); i++)
            {
                var line = main.Add.ScatterLine(Field, curves!.Curves[i], Palette.GetColor(i));
                line.LegendText = $"Peak {i + 1}";
                line.LinePattern = LinePattern.Dashed;
                line.LineWidth = i == SelectedIndex ? 3 : 1.5f;
            }
            if (fit is not null)
            {
                var sum = main.Add.ScatterLine(Field, fit, Colors.Red);
                sum.LegendText = "Fit";
                sum.LineWidth = 2;
            }
            main.YLabel(DataLabel);
            main.ShowLegend();
            if (markers is { } m)
                AddMarkers(main, m, markerColor);

            if (_rescale)
            {
                // Show the fit range with 5 % on each side; y fits the data and model there.
                var view = _range.View();
                var series = fit is null ? new[] { Signal } : new[] { Signal, fit };
                var (low, high) = PadExtent(view.Extent(Field, series));
                main.Axes.SetLimits(view.Min, view.Max, low, high);
                _rescale = false;
            }
            else
            {
                main.Axes.SetLimits(mainLimits);
            }
            x = main.Axes.GetLimits();
        }

        var residual = ResidualPlot.Plot;
        lock (residual.Sync)
        {
            residual.Clear();
            AddOutsideRange(residual);
            residual.Add.HorizontalLine(0, color: Colors.Black.WithAlpha(0.4), width: 1);
            if (dataMinusFit is not null)
                residual.Add.ScatterLine(Field, dataMinusFit, Colors.Red);
            residual.XLabel("Field (Oe)");
            residual.YLabel($"Data − fit ({ParameterUnits.Data(DataPower)})");
            if (markers is { } m)
                AddMarkers(residual, m, markerColor);

            // Residual pane: x follows the main pane (linked); y fits the residual visible in it.
            residual.Axes.SetLimitsX(x.Left, x.Right);
            var shown = new FieldRange(Math.Min(x.Left, x.Right), Math.Max(x.Left, x.Right));
            if (dataMinusFit is not null && shown.Extent(Field, dataMinusFit) is { } extent)
            {
                var (low, high) = PadExtent(extent);
                residual.Axes.SetLimitsY(low, high);
            }
            else
            {
                residual.Axes.AutoScaleY();
            }
        }

        MainPlot.Refresh();
        ResidualPlot.Refresh();
    }

    /// <summary>Greys out the fields below and above the fit range (out to the edges of the whole-spectrum view).</summary>
    private void AddOutsideRange(Plot plot)
    {
        var all = DataRange.View();
        var grey = Colors.Gray.WithAlpha(0.2);
        foreach (var (from, to) in new[] { (all.Min, _range.Min), (_range.Max, all.Max) })
        {
            if (to <= from)
                continue;
            var span = plot.Add.HorizontalSpan(from, to);
            span.FillStyle.Color = grey;
            span.LineStyle.Width = 0;
        }
    }

    /// <summary>Axis limits around [min, max] with 5 % padding; a small band if the extent is flat or missing.</summary>
    private static (double Low, double High) PadExtent((double Min, double Max)? extent)
    {
        if (extent is not { } e)
            return (-1, 1);
        var pad = e.Max > e.Min ? 0.05 * (e.Max - e.Min) : Math.Max(Math.Abs(e.Max) * 0.05, 1e-12);
        return (e.Min - pad, e.Max + pad);
    }

    /// <summary>Dashed line at the centre, dash-dotted lines at the left and right widths.</summary>
    private static void AddMarkers(Plot plot, PeakMarkers markers, ScottPlot.Color color)
    {
        var center = plot.Add.VerticalLine(markers.Center, width: 1.5f, color: color);
        center.LinePattern = LinePattern.Dashed;
        foreach (var x in new[] { markers.Left, markers.Right })
        {
            var side = plot.Add.VerticalLine(x, width: 1.5f, color: color);
            side.LinePattern = DashDot;
        }
    }

    private static string DescribePeak(int index, Peak peak, int dataPower)
    {
        var sb = new StringBuilder();
        sb.Append($"Peak {index + 1}: {peak.Shape.Name}");
        for (var i = 0; i < peak.Parameters.Length; i++)
        {
            var kind = peak.Shape.ParameterKinds[i];
            var value = kind == ParameterKind.Width ? Math.Abs(peak.Parameters[i]) : peak.Parameters[i];
            sb.Append($"\n   {peak.Shape.ParameterNames[i]} = {Format(value)}");
            if (peak.Errors is { } errors && double.IsFinite(errors[i]))
                sb.Append($" ± {Format(errors[i])}");
            sb.Append(' ').Append(ParameterUnits.OfData(kind, dataPower));
            if (peak.IsFixed(i))
                sb.Append(" (fixed)");
        }
        return sb.ToString();
    }

    private void ShowResult()
    {
        var notes = new[] { _status, _dirty ? "Unsaved changes." : null }.OfType<string>().ToList();
        ResultText.Text = DescribeResult(_result, DataPower) + (notes.Count == 0 ? "" : "\n\n" + string.Join("\n", notes));
    }

    /// <summary>
    /// Result text under the adjustments: goodness of fit (every point weighted 1, as in Origin), plus the optimizer's
    /// iterations right after a fit.
    /// </summary>
    private static string DescribeResult(FitResult? result, int dataPower)
    {
        if (result is null)
            return "No peaks yet.";
        var squared = ParameterUnits.DataSquared(dataPower);
        var text =
            $"Points N = {result.Points}, free parameters p = {result.FreeParameters}\n" +
            $"χ² = Σ(data − fit)² = {Format(result.ChiSquared)} {squared}\n" +
            $"Reduced χ² = χ²/(N − p) = {Format(result.ReducedChiSquared)} {squared}\n" +
            $"σ = √(reduced χ²) = {Format(result.StandardDeviation)} {ParameterUnits.Data(dataPower)}\n" +
            $"R² = {result.RSquared.ToString("0.#####", CultureInfo.InvariantCulture)}";
        text += result.Iterations > 0
            ? $"\nIterations: {result.Iterations} ({result.ExitReason})"
            : "\nNot fitted since the last change: press Fit.";
        return text;
    }

    /// <summary>Short display (5 significant digits) for the peak list and the result.</summary>
    private static string Format(double value) => value.ToString("G5", CultureInfo.InvariantCulture);
}
