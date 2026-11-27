using FmrFit.Core.IO;
using FmrFit.Core.Processing;

namespace FmrFit.Core.Tests.Processing;

public class SignalCalculusTests
{
    private static readonly double[] Grid = Enumerable.Range(0, 51).Select(i => 200 + 10.0 * i).ToArray();

    [Fact]
    public void Integral_starts_at_zero_and_is_exact_for_a_line()
    {
        // ∫(2 + 0.01 H) dH from 200 = 2 (H − 200) + 0.005 (H² − 200²); trapezoids are exact for a line.
        var values = Grid.Select(h => 2 + 0.01 * h).ToArray();

        var integral = SignalCalculus.Integrate(Grid, values);

        Assert.Equal(0, integral[0]);
        for (var i = 0; i < Grid.Length; i++)
            Assert.Equal(2 * (Grid[i] - 200) + 0.005 * (Grid[i] * Grid[i] - 200 * 200), integral[i], 9);
    }

    [Fact]
    public void Derivative_is_exact_for_a_parabola_inside_and_for_a_line_at_the_ends()
    {
        var parabola = Grid.Select(h => 3 - 0.2 * h + 1e-3 * h * h).ToArray();
        var line = Grid.Select(h => 5 - 0.3 * h).ToArray();

        var dParabola = SignalCalculus.Differentiate(Grid, parabola);
        var dLine = SignalCalculus.Differentiate(Grid, line);

        for (var i = 1; i < Grid.Length - 1; i++)
            Assert.Equal(-0.2 + 2e-3 * Grid[i], dParabola[i], 9);
        Assert.All(dLine, v => Assert.Equal(-0.3, v, 12));
    }

    [Fact]
    public void Derivative_handles_an_uneven_grid()
    {
        double[] field = [0, 1, 3, 4, 7, 8.5];
        var values = field.Select(h => h * h).ToArray();

        var d = SignalCalculus.Differentiate(field, values);

        for (var i = 1; i < field.Length - 1; i++)
            Assert.Equal(2 * field[i], d[i], 9);
    }

    [Fact]
    public void Derivative_of_the_integral_gives_back_a_smooth_signal()
    {
        var values = Grid.Select(h => Math.Sin(h / 80)).ToArray();

        var back = SignalCalculus.Differentiate(Grid, SignalCalculus.Integrate(Grid, values));

        // Discretization error of order (step / 80)² ≈ 1.6 %.
        for (var i = 1; i < Grid.Length - 1; i++)
            Assert.InRange(back[i] - values[i], -0.01, 0.01);
    }

    [Fact]
    public void Short_inputs_and_mismatched_lengths()
    {
        Assert.Equal([0.0], SignalCalculus.Differentiate([1.0], [5.0]));
        Assert.Equal([0.0], SignalCalculus.Integrate([1.0], [5.0]));
        Assert.Empty(SignalCalculus.Integrate([], []));
        Assert.Throws<ArgumentException>(() => SignalCalculus.Integrate([1.0, 2], [5.0]));
    }

    [Fact]
    public void Zero_minimum_offset_moves_the_lowest_value_to_zero()
    {
        Assert.Equal(3.5, SignalCalculus.ZeroMinimumOffset([2.0, -3.5, 1, double.NaN]));
        Assert.Equal(-2, SignalCalculus.ZeroMinimumOffset([2.0, 4]));
        Assert.Equal(0, SignalCalculus.ZeroMinimumOffset([]));
    }

    [Fact]
    public void Displayed_adds_the_offset_only_when_applied()
    {
        double[] field = [0, 1, 2, 3];
        double[] signal = [-1, -1, 2, 2];

        // Trapezoid integral 0, −1, −0.5, 1.5: the offset that moves its minimum to 0 is 1.
        var integral = SignalCalculus.View(field, signal, SignalView.Integral);
        var offset = SignalCalculus.ZeroMinimumOffset(integral);

        Assert.Equal(1, offset);
        Assert.Equal([1.0, 0, 0.5, 2.5], SignalCalculus.Displayed(field, signal, SignalView.Integral, true, offset));
        Assert.Equal(integral, SignalCalculus.Displayed(field, signal, SignalView.Integral, false, offset));
        Assert.Equal([-0.5, -0.5, 2.5, 2.5], SignalCalculus.Displayed(field, signal, SignalView.Signal, true, 0.5));
        Assert.NotSame(signal, SignalCalculus.Displayed(field, signal, SignalView.Signal, false, 0.5));
        Assert.Equal([-1.0, -1, 2, 2], signal); // input untouched
    }

    [Fact]
    public void Displayed_column_of_a_work_file_adds_the_stored_offset_only_when_applied()
    {
        var file = MapExportBuilderTests.File("S", 30, [0, 1, 2], [1, 2, 3]);
        var on = file with { Processing = file.Processing with { DisplayOffset = 10, DisplayOffsetApplied = true } };
        var off = on with { Processing = on.Processing with { DisplayOffsetApplied = false } };

        Assert.Equal([11.0, 12, 13], SignalCalculus.DisplayedColumn(on));
        Assert.Equal([1.0, 2, 3], SignalCalculus.DisplayedColumn(off));
        Assert.Equal([1.0, 2, 3], on.SignalMv); // the stored data never change
    }

    [Theory]
    [InlineData(RecordedSignal.Derivative, SignalView.Signal, false)]
    [InlineData(RecordedSignal.Derivative, SignalView.Integral, true)]
    [InlineData(RecordedSignal.Derivative, SignalView.Derivative, false)]
    [InlineData(RecordedSignal.Absorption, SignalView.Signal, true)]
    [InlineData(RecordedSignal.Absorption, SignalView.Derivative, true)]
    public void Offset_to_zero_is_on_by_default_for_integrals_and_recorded_P(RecordedSignal recorded, SignalView view, bool expected)
    {
        Assert.Equal(expected, SignalCalculus.DefaultApplyOffset(recorded, view));
    }

    [Theory]
    [InlineData(SignalView.Signal, "signal")]
    [InlineData(SignalView.Integral, "integral")]
    [InlineData(SignalView.Derivative, "derivative")]
    public void View_text_round_trips(SignalView view, string text)
    {
        Assert.Equal(text, SignalViewText.ToText(view));
        Assert.Equal(view, SignalViewText.Parse(text));
        Assert.Equal(SignalView.Signal, SignalViewText.Parse(null));
    }

    [Fact]
    public void Work_file_column_uses_stored_values_or_calculates_them_for_old_files()
    {
        double[] field = [0, 1, 2];
        double[] signal = [1, 1, 1];
        var old = MapExportBuilderTests.File("s", 30, field, signal);
        var stored = old with { IntegralMvOe = [9, 9, 9], DerivativeMvPerOe = [7, 7, 7] };

        Assert.Equal([0.0, 1, 2], SignalCalculus.Column(old, SignalView.Integral));
        Assert.Equal([0.0, 0, 0], SignalCalculus.Column(old, SignalView.Derivative));
        Assert.Equal([9.0, 9, 9], SignalCalculus.Column(stored, SignalView.Integral));
        Assert.Equal([7.0, 7, 7], SignalCalculus.Column(stored, SignalView.Derivative));
        Assert.Same(signal, SignalCalculus.Column(stored, SignalView.Signal));

        Assert.Equal(SignalView.Signal, SignalCalculus.DisplayedView(old));
        Assert.Equal(SignalView.Derivative,
            SignalCalculus.DisplayedView(old with { Processing = old.Processing with { Display = "derivative" } }));
    }
}
