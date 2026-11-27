using System.ComponentModel;
using FmrFit.Core.IO;

namespace FmrFit.App;

/// <summary>One row of the export window's file list: a readable work file and whether it is exported.</summary>
public sealed class ExportFileItem(WorkFileItem item, WorkFile file) : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public WorkFile File { get; } = file;

    public string Path => item.Path;

    public string FrequencyText => item.FrequencyText;

    public string Name => item.Name;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}
