namespace DSO1102.Controller.Core.Models;

/// <summary>
/// Vendor range and coupling codes captured in runtime traces of the original application
/// (docs/REVERSE_ENGINEERING.md, "Voltage/div and coupling runtime mapping"). Only these
/// codes may be sent; the other six ranges of the family table are not traced on this unit.
/// </summary>
public static class AnalogFrontEnd
{
    private static readonly (double VoltsPerDivision, int Code, string Label)[] TracedRanges =
    [
        (0.5, 5, "500 mV/div"),
        (1.0, 6, "1 V/div"),
        (2.0, 7, "2 V/div")
    ];

    public static IReadOnlyList<double> SupportedVoltsPerDivision { get; } =
        TracedRanges.Select(r => r.VoltsPerDivision).ToArray();

    public static bool IsTracedRangeCode(int code) => TracedRanges.Any(r => r.Code == code);

    /// <summary>0 = DC (also used for software GND), 1 = AC.</summary>
    public static bool IsTracedCouplingCode(int code) => code is 0 or 1;

    public static int RangeCode(double voltsPerDivision)
    {
        foreach (var range in TracedRanges)
        {
            if (range.VoltsPerDivision.Equals(voltsPerDivision))
                return range.Code;
        }

        throw new NotSupportedException(
            $"{voltsPerDivision} V/div is not runtime-verified. Supported: 500 mV/div, 1 V/div, 2 V/div.");
    }

    public static string RangeLabel(int code) =>
        TracedRanges.FirstOrDefault(r => r.Code == code).Label ?? $"code {code}";

    /// <summary>Ground uses the non-AC relay state; its samples are replaced in software.</summary>
    public static int CouplingCode(ChannelCoupling coupling) => coupling == ChannelCoupling.AC ? 1 : 0;

    public static string CouplingLabel(int code) => code == 1 ? "AC" : "DC";
}
