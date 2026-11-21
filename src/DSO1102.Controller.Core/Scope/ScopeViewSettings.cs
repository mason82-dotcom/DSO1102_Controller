using DSO1102.Controller.Core.Models;
using DSO1102.Controller.Core.SignalAnalysis;

namespace DSO1102.Controller.Core.Scope;

/// <summary>Software trigger mode applied to an already captured record.</summary>
public enum SoftwareTriggerMode
{
    /// <summary>Show the record centre when no edge is found.</summary>
    Auto,
    /// <summary>Only accept frames with a qualifying edge.</summary>
    Normal
}

/// <summary>Display, trigger and analysis settings. These never reach the instrument.</summary>
public sealed record ScopeViewSettings
{
    public const int HorizontalDivisions = 10;
    public const int VerticalDivisions = 8;

    public double TimePerDivisionSeconds { get; init; } = 0.001;

    /// <summary>Trigger point position inside the visible window, 0..100 %.</summary>
    public double TriggerPositionPercent { get; init; } = 50;

    public SoftwareTriggerMode TriggerMode { get; init; } = SoftwareTriggerMode.Auto;
    public TriggerSource TriggerSource { get; init; } = TriggerSource.Channel1;
    public TriggerSlope TriggerSlope { get; init; } = TriggerSlope.Rising;

    /// <summary>Trigger level in sample units (ADC counts or volts).</summary>
    public double TriggerLevel { get; init; } = 128;

    /// <summary>Hysteresis as a fraction of the source peak-to-peak value.</summary>
    public double TriggerHysteresisFraction { get; init; } = 0.03;

    public ChannelView Channel1 { get; init; } = new();
    public ChannelView Channel2 { get; init; } = new();

    public WindowFunction FftWindow { get; init; } = WindowFunction.Hann;
    public bool ShowSpectrum { get; init; } = true;

    public double WindowDurationSeconds => TimePerDivisionSeconds * HorizontalDivisions;

    public ScopeViewSettings Normalize()
    {
        static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;

        return this with
        {
            TimePerDivisionSeconds = double.IsFinite(TimePerDivisionSeconds) && TimePerDivisionSeconds > 0 ? TimePerDivisionSeconds : 0.001,
            TriggerPositionPercent = Math.Clamp(Finite(TriggerPositionPercent, 50), 0, 100),
            TriggerLevel = Finite(TriggerLevel, 0),
            TriggerHysteresisFraction = Math.Clamp(Finite(TriggerHysteresisFraction, 0.03), 0, 0.5),
            Channel1 = (Channel1 ?? new()).Normalize(),
            Channel2 = (Channel2 ?? new()).Normalize()
        };
    }
}

/// <summary>Per-channel display state.</summary>
public sealed record ChannelView
{
    public bool Visible { get; init; } = true;

    /// <summary>Sample units per vertical division.</summary>
    public double UnitsPerDivision { get; init; } = 32;

    /// <summary>Sample value drawn on the horizontal centre line.</summary>
    public double CenterValue { get; init; } = 128;

    public ChannelView Normalize() => this with
    {
        UnitsPerDivision = double.IsFinite(UnitsPerDivision) && UnitsPerDivision > 0 ? UnitsPerDivision : 32,
        CenterValue = double.IsFinite(CenterValue) ? CenterValue : 0
    };
}
