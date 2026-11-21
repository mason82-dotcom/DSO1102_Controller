namespace DSO1102.Controller.Core.Scope;

/// <summary>1-2-5 display time bases. They select a window of the captured record.</summary>
public static class TimeBaseSteps
{
    public static IReadOnlyList<double> All { get; } =
    [
        5e-6, 10e-6, 20e-6, 50e-6,
        100e-6, 200e-6, 500e-6,
        1e-3, 2e-3, 5e-3, 10e-3
    ];

    /// <summary>Steps whose 10-division window fits completely into a record.</summary>
    public static IReadOnlyList<double> FittingRecord(int sampleCount, double sampleRate)
    {
        if (sampleCount < 2 || !double.IsFinite(sampleRate) || sampleRate <= 0)
            return All;

        var span = (sampleCount - 1) / sampleRate;
        var fitting = All.Where(step => step * ScopeViewSettings.HorizontalDivisions <= span * (1 + 1e-9)).ToArray();
        return fitting.Length > 0 ? fitting : [All[0]];
    }

    public static string Format(double secondsPerDivision) => $"{FormatTime(secondsPerDivision)}/div";

    public static string FormatTime(double seconds)
    {
        var abs = Math.Abs(seconds);
        return abs switch
        {
            0 => "0 s",
            >= 1 => $"{seconds:0.###} s",
            >= 1e-3 => $"{seconds * 1e3:0.###} ms",
            >= 1e-6 => $"{seconds * 1e6:0.###} µs",
            _ => $"{seconds * 1e9:0.###} ns"
        };
    }

    public static string FormatFrequency(double hz)
    {
        if (!double.IsFinite(hz) || hz <= 0)
            return "—";

        return hz switch
        {
            >= 1e6 => $"{hz / 1e6:F3} MHz",
            >= 1e3 => $"{hz / 1e3:F3} kHz",
            _ => $"{hz:F2} Hz"
        };
    }
}
