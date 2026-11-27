using FmrFit.Core.IO;
using MathNet.Numerics;

namespace FmrFit.Core.Processing;

/// <summary>One monotonic-ish part of a field sweep (up or down), in recorded order.</summary>
public sealed record SweepBranch(double[] Time, double[] Field, double[] X, double[] Y)
{
    public int Count => Time.Length;
}

/// <summary>
/// A measurement split into its up and down sweeps, with the field sweep rate of each.
/// </summary>
/// <param name="UpRate">dH/dt of the up sweep (Oe/s, positive).</param>
/// <param name="DownRate">dH/dt of the down sweep (Oe/s, negative).</param>
public sealed record FieldSweeps(SweepBranch Up, SweepBranch Down, double UpRate, double DownRate)
{
    private const int MinBranchPoints = 8;

    /// <summary>
    /// Splits at the highest field: everything up to and including it is the up sweep,
    /// everything after it is the down sweep. Expects a sweep that goes up first, then down.
    /// </summary>
    public static FieldSweeps FromMeasurement(FmrMeasurement m)
    {
        var turn = 0;
        for (var i = 1; i < m.Count; i++)
            if (m.Field[i] > m.Field[turn])
                turn = i;

        var upCount = turn + 1;
        var downCount = m.Count - upCount;
        if (upCount < MinBranchPoints || downCount < MinBranchPoints)
            throw new InvalidOperationException(
                $"Expected an up sweep followed by a down sweep; the highest field is at point {turn} of {m.Count}.");

        var up = Slice(m, 0, upCount);
        var down = Slice(m, upCount, downCount);
        return new FieldSweeps(up, down, MiddleHalfRate(up), MiddleHalfRate(down));
    }

    /// <summary>dH/dt from a straight-line fit of field versus time over the middle half (25–75 %) of the branch.</summary>
    public static double MiddleHalfRate(SweepBranch branch)
    {
        var start = branch.Count / 4;
        var end = branch.Count * 3 / 4;
        var (_, slope) = Fit.Line(branch.Time[start..end], branch.Field[start..end]);
        return slope;
    }

    private static SweepBranch Slice(FmrMeasurement m, int start, int count) => new(
        m.Time.AsSpan(start, count).ToArray(),
        m.Field.AsSpan(start, count).ToArray(),
        m.X.AsSpan(start, count).ToArray(),
        m.Y.AsSpan(start, count).ToArray());
}
