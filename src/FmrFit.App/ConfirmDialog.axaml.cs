using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FmrFit.App;

/// <summary>
/// Modal yes/no question (<see cref="Ask"/>) or message with only OK (<see cref="Inform"/>). Cancel, Escape and
/// closing the window all count as "no".
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>Shows <paramref name="message"/> over <paramref name="owner"/>; true if the user confirmed.</summary>
    public static Task<bool> Ask(Window owner, string title, string message, string confirmText)
    {
        var dialog = new ConfirmDialog { Title = title };
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmText;
        return dialog.ShowDialog<bool>(owner);
    }

    /// <summary>Shows <paramref name="message"/> over <paramref name="owner"/> with an OK button only.</summary>
    public static Task Inform(Window owner, string title, string message)
    {
        var dialog = new ConfirmDialog { Title = title };
        dialog.MessageText.Text = message;
        dialog.CancelButton.IsVisible = false;
        // Escape still closes it: IsCancel moves to OK.
        dialog.ConfirmButton.IsCancel = true;
        return dialog.ShowDialog(owner);
    }

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}
