namespace FmrFit.Core.Fitting;

/// <summary>
/// The field range [Min, Max] (Oe) used for fitting: only points inside it are fitted and counted in the statistics.
/// Min &lt; Max always holds for ranges made by <see cref="Of"/> or <see cref="Create"/>.
/// </summary>
public readonly record struct FieldRange(double Min, double Max)
{
    /// <summary>Margin on each side of the range in <see cref="View"/>, as a fraction of its width (total width 1.1×).</summary>
    public const double ViewMargin = 0.05;

    public double Width => Max - Min;

    /// <summary>The full range of <paramref name="field"/>; null if it has fewer than two distinct values.</summary>
    public static FieldRange? Of(double[] field)
    {
        var finite = field.Where(double.IsFinite).ToArray();
        if (finite.Length == 0)
            return null;
        var (min, max) = (finite.Min(), finite.Max());
        return min < max ? new FieldRange(min, max) : null;
    }

    /// <summary>
    /// [<paramref name="min"/>, <paramref name="max"/>] with both ends clamped into <paramref name="bounds"/>; null if
    /// the clamped minimum is not below the clamped maximum or an end is not a number.
    /// </summary>
    public static FieldRange? Create(double min, double max, FieldRange bounds)
    {
        if (double.IsNaN(min) || double.IsNaN(max))
            return null;
        var (a, b) = (Math.Clamp(min, bounds.Min, bounds.Max), Math.Clamp(max, bounds.Min, bounds.Max));
        return a < b ? new FieldRange(a, b) : null;
    }

    public bool Contains(double field) => field >= Min && field <= Max;

    /// <summary>The points whose field lies inside the range (ends included), in their original order.</summary>
    public (double[] Field, double[] Signal) Slice(double[] field, double[] signal)
    {
        if (field.Length != signal.Length)
            throw new ArgumentException("Field and signal must have the same length.");
        var range = this;
        var inside = Enumerable.Range(0, field.Length).Where(i => range.Contains(field[i])).ToArray();
        return (inside.Select(i => field[i]).ToArray(), inside.Select(i => signal[i]).ToArray());
    }

    /// <summary>The range widened by <see cref="ViewMargin"/> of its width on each side: the x-axis that shows it.</summary>
    public FieldRange View() => new(Min - ViewMargin * Width, Max + ViewMargin * Width);

    /// <summary>
    /// Smallest and largest finite value of the <paramref name="series"/> (each indexed like
    /// <paramref name="field"/>) at fields inside the range; null if there are none.
    /// </summary>
    public (double Min, double Max)? Extent(double[] field, params double[][] series)
    {
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        for (var i = 0; i < field.Length; i++)
        {
            if (!Contains(field[i]))
                continue;
            foreach (var s in series)
            {
                if (i >= s.Length || !double.IsFinite(s[i]))
                    continue;
                min = Math.Min(min, s[i]);
                max = Math.Max(max, s[i]);
            }
        }
        return min <= max ? (min, max) : null;
    }
}
