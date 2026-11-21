using System.Text;
using DSO1102.Controller.Core.Scope;

namespace DSO1102.Controller.Core.Presentation;

/// <summary>Time cursors A/B: times are relative to the trigger point of a <see cref="ScopeView"/>.</summary>
public static class CursorReadout
{
    public const string Hint = "Linksklick setzt A, Rechtsklick setzt B.";

    /// <summary>Nearest sample of <paramref name="samples"/> at <paramref name="time"/>, or null outside the record.</summary>
    public static double? ValueAt(ScopeView view, IReadOnlyList<double> samples, double time)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(samples);
        var index = Math.Round(view.WindowStart + (time - view.WindowStartTimeSeconds) * view.SampleRate);
        return index >= 0 && index < samples.Count ? samples[(int)index] : null;
    }

    /// <summary>Converts a horizontal fraction of the plot (0..1) into a cursor time.</summary>
    public static double TimeAtFraction(ScopeView view, double fraction)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.WindowStartTimeSeconds + Math.Clamp(fraction, 0, 1) * view.WindowDurationSeconds;
    }

    /// <summary>Moves a cursor by whole or fractional divisions, clamped to the visible window.</summary>
    public static double Move(ScopeView view, double? current, double divisions)
    {
        ArgumentNullException.ThrowIfNull(view);
        var step = view.WindowDurationSeconds / ScopeViewSettings.HorizontalDivisions;
        return Clamp(view, (current ?? 0) + divisions * step);
    }

    public static double Clamp(ScopeView view, double time)
    {
        ArgumentNullException.ThrowIfNull(view);
        return Math.Clamp(time, view.WindowStartTimeSeconds, view.WindowStartTimeSeconds + view.WindowDurationSeconds);
    }

    public static string Describe(ScopeView? view, double? cursorA, double? cursorB)
    {
        if (view is null || (cursorA is null && cursorB is null))
            return Hint;

        var domain = view.Frame.SampleDomain;
        var text = new StringBuilder();

        void Append(string name, double? time)
        {
            if (time is not { } t) return;
            text.Append(name).Append(": t ").AppendLine(TimeBaseSteps.FormatTime(t).PadLeft(11));
            text.Append("   CH1 ").Append(DisplayFormat.Value(ValueAt(view, view.Frame.Channel1, t), domain))
                .Append("  CH2 ").AppendLine(DisplayFormat.Value(ValueAt(view, view.Frame.Channel2, t), domain));
        }

        Append("A", cursorA);
        Append("B", cursorB);

        if (cursorA is { } a && cursorB is { } b)
        {
            var dt = Math.Abs(b - a);
            text.Append("Δt ").Append(TimeBaseSteps.FormatTime(dt))
                .Append("   1/Δt ").AppendLine(TimeBaseSteps.FormatFrequency(dt > 0 ? 1 / dt : 0));
            var d1 = ValueAt(view, view.Frame.Channel1, b) - ValueAt(view, view.Frame.Channel1, a);
            var d2 = ValueAt(view, view.Frame.Channel2, b) - ValueAt(view, view.Frame.Channel2, a);
            text.Append("ΔCH1 ").Append(DisplayFormat.Value(d1, domain))
                .Append("  ΔCH2 ").Append(DisplayFormat.Value(d2, domain));
        }

        return text.ToString().TrimEnd();
    }
}
