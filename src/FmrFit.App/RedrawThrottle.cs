using System.Diagnostics;
using Avalonia.Threading;

namespace FmrFit.App;

/// <summary>
/// Limits redraws after input to <c>max_redraws_per_second</c> (settings), shared by all windows so slider drags and
/// mouse-wheel steps update the graphs alike: a request within that interval of the last redraw schedules one redraw
/// for the end of the interval, which then shows the latest values.
/// </summary>
public sealed class RedrawThrottle
{
    private readonly DispatcherTimer _timer = new();
    private readonly Stopwatch _sinceRedraw = Stopwatch.StartNew();
    private readonly Action _redraw;
    private readonly Func<double> _maxPerSecond;

    /// <param name="redraw">The window's redraw; it must call <see cref="Redrawing"/> first.</param>
    /// <param name="maxPerSecond">Current redraw limit, read at each request.</param>
    public RedrawThrottle(Action redraw, Func<double> maxPerSecond)
    {
        _redraw = redraw;
        _maxPerSecond = maxPerSecond;
        _timer.Tick += (_, _) => _redraw();
    }

    /// <summary>Redraws now, or once at the end of the current interval.</summary>
    public void Request()
    {
        if (_timer.IsEnabled)
            return;
        var wait = TimeSpan.FromSeconds(1 / _maxPerSecond()) - _sinceRedraw.Elapsed;
        if (wait <= TimeSpan.Zero)
        {
            _redraw();
            return;
        }
        _timer.Interval = wait;
        _timer.Start();
    }

    /// <summary>Called by every redraw (requested or not): cancels a pending one and starts a new interval.</summary>
    public void Redrawing()
    {
        _timer.Stop();
        _sinceRedraw.Restart();
    }

    /// <summary>Cancels a pending redraw (when the window closes).</summary>
    public void Stop() => _timer.Stop();
}
