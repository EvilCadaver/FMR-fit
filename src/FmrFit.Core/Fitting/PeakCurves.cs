namespace FmrFit.Core.Fitting;

/// <summary>
/// Each peak evaluated over a whole field array, and their sum. <see cref="Update"/> evaluates only the peaks whose
/// values changed and takes the others' curves from the previous instance, so changing one parameter of one peak costs
/// one evaluation of that peak. Instances and their arrays are never modified.
/// </summary>
public sealed class PeakCurves
{
    private PeakCurves(double[] field, IReadOnlyList<Peak> peaks, IReadOnlyList<double[]> curves, double[] sum, int evaluated)
    {
        Field = field;
        Peaks = peaks;
        Curves = curves;
        Sum = sum;
        Evaluated = evaluated;
    }

    /// <summary>No peaks and no field: the start for <see cref="Update"/>.</summary>
    public static PeakCurves Empty { get; } = new([], [], [], [], 0);

    /// <summary>The field array the curves were evaluated on.</summary>
    public double[] Field { get; }

    public IReadOnlyList<Peak> Peaks { get; }

    /// <summary>Each peak at every field, in the order of <see cref="Peaks"/>.</summary>
    public IReadOnlyList<double[]> Curves { get; }

    /// <summary>Sum of all peaks at every field (the model); added in peak order, as in <see cref="PeakFitter.Evaluate"/>.</summary>
    public double[] Sum { get; }

    /// <summary>How many peaks the <see cref="Update"/> that made this instance had to evaluate.</summary>
    public int Evaluated { get; }

    /// <summary>
    /// The curves of <paramref name="peaks"/> on <paramref name="field"/>. On the same field array, a peak with the same
    /// shape and parameter values as one here reuses its curve (each stored curve at most once); the rest are evaluated.
    /// Limits, fixing and errors don't affect a curve, so changing only those evaluates nothing.
    /// </summary>
    public PeakCurves Update(double[] field, IReadOnlyList<Peak> peaks)
    {
        var sameField = ReferenceEquals(field, Field);
        var used = new bool[Peaks.Count];
        var curves = new double[peaks.Count][];
        var evaluated = 0;
        for (var i = 0; i < peaks.Count; i++)
        {
            var stored = sameField ? FindUnused(peaks[i], used) : -1;
            if (stored >= 0)
            {
                used[stored] = true;
                curves[i] = Curves[stored];
            }
            else
            {
                curves[i] = peaks[i].Evaluate(field);
                evaluated++;
            }
        }

        var sum = new double[field.Length];
        foreach (var curve in curves)
            for (var j = 0; j < sum.Length; j++)
                sum[j] += curve[j];
        return new PeakCurves(field, peaks.ToArray(), curves, sum, evaluated);
    }

    /// <summary>
    /// Statistics of the peaks over the points of <paramref name="signal"/> (indexed like <see cref="Field"/>) inside
    /// <paramref name="range"/>; the same numbers as <see cref="PeakFitter.Evaluate"/> on that slice.
    /// </summary>
    public FitResult Compare(double[] signal, FieldRange range)
    {
        var (_, data) = range.Slice(Field, signal);
        var (_, fit) = range.Slice(Field, Sum);
        return PeakFitter.Compare(data, fit, Peaks);
    }

    /// <summary>Data − <see cref="Sum"/> at every field; <paramref name="signal"/> is indexed like <see cref="Field"/>.</summary>
    public double[] Residual(double[] signal)
    {
        if (signal.Length != Sum.Length)
            throw new ArgumentException("Signal must be indexed like the field.");
        return signal.Select((v, j) => v - Sum[j]).ToArray();
    }

    private int FindUnused(Peak peak, bool[] used)
    {
        for (var k = 0; k < Peaks.Count; k++)
            if (!used[k] && Peaks[k].Shape.Id == peak.Shape.Id && Peaks[k].Parameters.SequenceEqual(peak.Parameters))
                return k;
        return -1;
    }
}
