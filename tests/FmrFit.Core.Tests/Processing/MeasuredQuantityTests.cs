using FmrFit.Core.Processing;

namespace FmrFit.Core.Tests.Processing;

public class MeasuredQuantityTests
{
    [Theory]
    [InlineData(RecordedSignal.Derivative, SignalView.Integral, 0, "P")]
    [InlineData(RecordedSignal.Derivative, SignalView.Signal, 1, "dP/dH")]
    [InlineData(RecordedSignal.Derivative, SignalView.Derivative, 2, "d²P/dH²")]
    [InlineData(RecordedSignal.Absorption, SignalView.Integral, -1, "∫P dH")]
    [InlineData(RecordedSignal.Absorption, SignalView.Signal, 0, "P")]
    [InlineData(RecordedSignal.Absorption, SignalView.Derivative, 1, "dP/dH")]
    public void Recorded_signal_and_display_give_the_quantity(RecordedSignal recorded, SignalView view, int order, string name)
    {
        Assert.Equal(order, MeasuredQuantity.Order(recorded, view));
        Assert.Equal(name, MeasuredQuantity.Name(order));
    }

    [Theory]
    [InlineData(-1, "IntPdH")]
    [InlineData(0, "P")]
    [InlineData(1, "dPdH")]
    [InlineData(2, "d2PdH2")]
    public void Column_names_are_plain_ascii(int order, string column)
    {
        Assert.Equal(column, MeasuredQuantity.ColumnName(order));
    }

    [Theory]
    [InlineData(0, "mV")]
    [InlineData(1, "mV_Oe")]
    [InlineData(-1, "mV_per_Oe")]
    [InlineData(2, "mV_Oe2")]
    public void Unit_suffixes(int power, string suffix)
    {
        Assert.Equal(suffix, MeasuredQuantity.UnitSuffix(power));
    }

    [Theory]
    [InlineData(RecordedSignal.Derivative, "dP/dH")]
    [InlineData(RecordedSignal.Absorption, "~P")]
    public void Recorded_text_round_trips_and_defaults_to_dP_dH(RecordedSignal recorded, string text)
    {
        Assert.Equal(text, RecordedSignalText.ToText(recorded));
        Assert.Equal(recorded, RecordedSignalText.Parse(text));
        Assert.Equal(RecordedSignal.Absorption, RecordedSignalText.Parse("p"));
        Assert.Equal(RecordedSignal.Derivative, RecordedSignalText.Parse(null));
    }

    [Fact]
    public void Order_of_a_work_file_uses_its_recorded_signal_and_display()
    {
        var file = MapExportBuilderTests.File("S", 30, [1], [1]);

        Assert.Equal(1, MeasuredQuantity.Order(file));
        Assert.Equal(0, MeasuredQuantity.Order(file with { Recorded = "~P" }));
        Assert.Equal(0, MeasuredQuantity.Order(file with { Processing = file.Processing with { Display = "integral" } }));
    }
}
