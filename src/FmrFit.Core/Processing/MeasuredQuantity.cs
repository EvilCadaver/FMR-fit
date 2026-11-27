using System.Globalization;
using FmrFit.Core.IO;

namespace FmrFit.Core.Processing;

/// <summary>What the lock-in recorded: the field derivative of the absorbed power (field modulation) or a signal
/// proportional to the absorbed power itself.</summary>
public enum RecordedSignal
{
    /// <summary>dP/dH, e.g. field modulation with lock-in detection.</summary>
    Derivative,

    /// <summary>A signal proportional to the absorbed power P, e.g. amplitude modulation.</summary>
    Absorption,
}

/// <summary>Text form of <see cref="RecordedSignal"/> as stored in work files.</summary>
public static class RecordedSignalText
{
    public static string ToText(RecordedSignal recorded) => recorded == RecordedSignal.Absorption ? "~P" : "dP/dH";

    /// <summary>Inverse of <see cref="ToText"/> ("P" also accepted); anything else, including a missing entry, is dP/dH.</summary>
    public static RecordedSignal Parse(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "~P" or "P" => RecordedSignal.Absorption,
        _ => RecordedSignal.Derivative,
    };
}

/// <summary>
/// The physical quantity a work file's displayed column stands for, as its order of field derivative of the absorbed
/// power P: −1 = ∫P dH, 0 = P, 1 = dP/dH, 2 = d²P/dH². dP/dH integrated and ~P as recorded are both P, so they can
/// go in one 3D export.
/// </summary>
public static class MeasuredQuantity
{
    public static int Order(RecordedSignal recorded, SignalView view) =>
        (recorded == RecordedSignal.Derivative ? 1 : 0) + SignalViewOrder(view);

    /// <summary>Order of the column the work file displays, given what was recorded.</summary>
    public static int Order(WorkFile file) =>
        Order(RecordedSignalText.Parse(file.Recorded), SignalCalculus.DisplayedView(file));

    /// <summary>Readable name: "∫P dH", "P", "dP/dH", "d²P/dH²".</summary>
    public static string Name(int order) => order switch
    {
        -1 => "∫P dH",
        0 => "P",
        1 => "dP/dH",
        2 => "d²P/dH²",
        _ => order < 0 ? $"∫^{-order}P dH^{-order}" : $"d^{order}P/dH^{order}",
    };

    /// <summary>Plain-ASCII name for a column header: "IntPdH", "P", "dPdH", "d2PdH2".</summary>
    public static string ColumnName(int order) => order switch
    {
        0 => "P",
        < 0 => order == -1 ? "IntPdH" : $"Int{-order}PdH{-order}",
        _ => order == 1 ? "dPdH" : $"d{order}PdH{order}",
    };

    /// <summary>Plain-ASCII unit suffix for mV·Oe^<paramref name="fieldPower"/>: "mV", "mV_Oe", "mV_per_Oe", "mV_Oe2".</summary>
    public static string UnitSuffix(int fieldPower)
    {
        if (fieldPower == 0)
            return "mV";
        var n = Math.Abs(fieldPower);
        return (fieldPower > 0 ? "mV_Oe" : "mV_per_Oe") + (n == 1 ? "" : n.ToString(CultureInfo.InvariantCulture));
    }

    private static int SignalViewOrder(SignalView view) => view switch
    {
        SignalView.Integral => -1,
        SignalView.Derivative => 1,
        _ => 0,
    };
}
