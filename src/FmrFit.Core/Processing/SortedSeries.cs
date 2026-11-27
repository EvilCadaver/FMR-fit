namespace FmrFit.Core.Processing;

/// <summary>
/// Values sorted by field, with points at identical field averaged, for linear interpolation.
/// Sorting makes the noisy, slightly non-monotonic start of a sweep usable.
/// </summary>
internal sealed class SortedSeries
{
    private readonly double[] _field;
    private readonly double[] _values;

    public SortedSeries(double[] field, double[] values)
    {
        if (field.Length != values.Length || field.Length == 0)
            throw new ArgumentException("Field and values must have the same, non-zero length.");

        var order = Enumerable.Range(0, field.Length).OrderBy(i => field[i]).ToArray();
        var f = new List<double>();
        var v = new List<double>();
        var k = 0;
        while (k < order.Length)
        {
            var h = field[order[k]];
            var sum = 0.0;
            var n = 0;
            for (; k < order.Length && field[order[k]] == h; k++, n++)
                sum += values[order[k]];
            f.Add(h);
            v.Add(sum / n);
        }
        _field = [.. f];
        _values = [.. v];
    }

    public double MinField => _field[0];
    public double MaxField => _field[^1];

    public bool Covers(double h) => h >= MinField && h <= MaxField;

    /// <summary>Linear interpolation; outside the covered range the nearest end segment is extended.</summary>
    public double Evaluate(double h)
    {
        if (_field.Length == 1)
            return _values[0];

        var i = Array.BinarySearch(_field, h);
        if (i >= 0)
            return _values[i];

        var upper = Math.Clamp(~i, 1, _field.Length - 1);
        var lower = upper - 1;
        var t = (h - _field[lower]) / (_field[upper] - _field[lower]);
        return _values[lower] + t * (_values[upper] - _values[lower]);
    }
}
