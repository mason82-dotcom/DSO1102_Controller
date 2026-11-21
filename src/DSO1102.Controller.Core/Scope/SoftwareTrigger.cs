using DSO1102.Controller.Core.Models;

namespace DSO1102.Controller.Core.Scope;

/// <summary>
/// Edge search on a captured record. The instrument itself is still force-triggered;
/// this only aligns the display, so it is labelled "software trigger" in the UI.
/// </summary>
public static class SoftwareTrigger
{
    /// <summary>
    /// Returns the first sample index in [searchStart, searchEnd) where the signal crosses
    /// <paramref name="level"/> in the requested direction after first leaving the
    /// hysteresis band on the opposite side. Returns null when there is no such edge.
    /// </summary>
    public static int? FindEdge(
        IReadOnlyList<double> samples,
        double level,
        TriggerSlope slope,
        double hysteresis,
        int searchStart,
        int searchEnd)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (!double.IsFinite(level))
            return null;

        hysteresis = double.IsFinite(hysteresis) ? Math.Max(0, hysteresis) : 0;
        searchStart = Math.Max(1, searchStart);
        searchEnd = Math.Min(samples.Count, searchEnd);

        if (searchStart >= searchEnd)
            return null;

        // Arm on samples before the search window as well, so an edge right at
        // searchStart is still recognised.
        var armed = false;
        var armScanStart = Math.Max(0, searchStart - 1);
        for (var i = armScanStart; i < searchEnd; i++)
        {
            var value = samples[i];
            if (slope == TriggerSlope.Rising)
            {
                if (value < level - hysteresis || (hysteresis == 0 && value < level))
                    armed = true;
                else if (armed && value >= level && i >= searchStart)
                    return i;
            }
            else
            {
                if (value > level + hysteresis || (hysteresis == 0 && value > level))
                    armed = true;
                else if (armed && value <= level && i >= searchStart)
                    return i;
            }
        }

        return null;
    }
}
