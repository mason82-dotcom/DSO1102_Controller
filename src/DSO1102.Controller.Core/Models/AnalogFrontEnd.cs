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

        throw new NotSupportedException($"{voltsPerDivision} V/div is not runtime-verified. Supported: {SupportedRangesText}.");
    }

    /// <summary>"500 mV/div (5), 1 V/div (6), 2 V/div (7)" — for error messages.</summary>
    public static string SupportedRangesText { get; } =
        string.Join(", ", TracedRanges.Select(r => $"{r.Label} ({r.Code})"));

    public static string RangeLabel(int code)
    {
        foreach (var range in TracedRanges)
        {
            if (range.Code == code)
                return range.Label;
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"code {code}");
    }

    /// <summary>
    /// Parses "frame-1ms-analog r1 r2 c1 c2" arguments and accepts only traced codes.
    /// Shared by the bridge (before any vendor call) and the client allow-list.
    /// </summary>
    public static bool TryParseFrameArguments(string arguments, out (ushort Range1, ushort Range2, ushort Coupling1, ushort Coupling2) codes, out string? error)
    {
        codes = default;
        var parts = arguments.Split(' ');
        var values = new ushort[4];
        var numeric = parts.Length == 4;
        for (var i = 0; numeric && i < 4; i++)
            numeric = ushort.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out values[i]);

        if (!numeric)
        {
            error = "Expected four numeric arguments: ch1Range ch2Range ch1Coupling ch2Coupling.";
            return false;
        }

        if (!IsTracedRangeCode(values[0]) || !IsTracedRangeCode(values[1]))
        {
            error = $"Only runtime-traced range codes are allowed: {SupportedRangesText}.";
            return false;
        }

        if (!IsTracedCouplingCode(values[2]) || !IsTracedCouplingCode(values[3]))
        {
            error = "Only coupling codes 0 (DC) and 1 (AC) are allowed.";
            return false;
        }

        codes = (values[0], values[1], values[2], values[3]);
        error = null;
        return true;
    }

    /// <summary>Ground uses the non-AC relay state; its samples are replaced in software.</summary>
    public static int CouplingCode(ChannelCoupling coupling) => coupling == ChannelCoupling.AC ? 1 : 0;

    public static string CouplingLabel(int code) => code == 1 ? "AC" : "DC";
}
