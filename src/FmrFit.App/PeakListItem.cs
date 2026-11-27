using System.ComponentModel;

namespace FmrFit.App;

/// <summary>One row of the fitting window's peak list; its text changes live while the peak is adjusted.</summary>
public sealed class PeakListItem(string text) : INotifyPropertyChanged
{
    private string _text = text;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
                return;
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }
}
