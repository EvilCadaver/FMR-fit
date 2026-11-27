using Avalonia.Controls;
using FmrFit.Core.IO;

namespace FmrFit.App;

/// <summary>
/// The user settings of this session (<c>fmr-fit.ini</c>), owned by the main window and handed to the dialogs.
/// The file is written at start-up only if it is missing or incomplete; later changes are kept in memory and written
/// by <see cref="Save"/> when the program exits, only if something changed. Without a path (no usable root) nothing is
/// ever written.
/// </summary>
public sealed class SettingsStore
{
    private readonly string? _path;
    private bool _dirty;

    private SettingsStore(string? path, AppSettings current)
    {
        _path = path;
        Current = current;
    }

    public AppSettings Current { get; private set; }

    /// <summary>Why the start-up write of a missing or incomplete file failed; null if it was not needed or succeeded.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Defaults only, never written (when the settings file has no usable location).</summary>
    public static SettingsStore InMemory() => new(null, AppSettings.Default);

    /// <summary>
    /// Loads <paramref name="path"/>; if it is missing, unreadable or incomplete it is rewritten complete at once,
    /// keeping the valid values. If that write fails, <see cref="Save"/> tries again on exit.
    /// </summary>
    public static SettingsStore Load(string path)
    {
        var (settings, complete) = AppSettingsFile.Load(path);
        var store = new SettingsStore(path, settings);
        if (!complete)
        {
            store._dirty = true;
            store.LoadError = store.Write();
        }
        return store;
    }

    /// <summary>Applies <paramref name="change"/> in memory; <see cref="Save"/> writes it.</summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        var changed = change(Current);
        if (changed == Current)
            return;
        Current = changed;
        _dirty = true;
    }

    /// <summary>
    /// Writes the file if anything changed since it was loaded; call once when the program exits. A file that cannot
    /// be written is left as it is (there is no window left to report it in).
    /// </summary>
    public void Save()
    {
        if (_dirty)
            Write();
    }

    /// <summary>Writes the file and clears the changed flag; the error message if it cannot be written.</summary>
    private string? Write()
    {
        if (_path is null)
            return null;
        try
        {
            AppSettingsFile.Save(_path, Current);
            _dirty = false;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>Gives <paramref name="window"/> the shared window size; call before it is shown.</summary>
    public void ApplyWindowSize(Window window)
    {
        var size = Current.Window;
        window.Width = size.Width;
        window.Height = size.Height;
        window.WindowState = size.Maximized ? WindowState.Maximized : WindowState.Normal;
    }

    /// <summary>
    /// Stores the size of <paramref name="window"/> as the shared window size; call when it closes. A maximized
    /// window keeps the stored normal size and only sets the maximized flag; a minimized one changes nothing.
    /// </summary>
    public void CaptureWindowSize(Window window)
    {
        var (width, height) = (window.ClientSize.Width, window.ClientSize.Height);
        var size = window.WindowState switch
        {
            WindowState.Maximized or WindowState.FullScreen => Current.Window with { Maximized = true },
            WindowState.Normal when double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0 =>
                new WindowSize(
                    Math.Clamp(Math.Round(width), AppSettingsFile.MinWindowWidth, AppSettingsFile.MaxWindowSize),
                    Math.Clamp(Math.Round(height), AppSettingsFile.MinWindowHeight, AppSettingsFile.MaxWindowSize),
                    Maximized: false),
            _ => Current.Window,
        };
        Update(s => s with { Window = size });
    }
}
