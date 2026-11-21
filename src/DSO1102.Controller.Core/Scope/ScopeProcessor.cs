using DSO1102.Controller.Core.Models;
using DSO1102.Controller.Core.SignalAnalysis;

namespace DSO1102.Controller.Core.Scope;

public enum TriggerState
{
    /// <summary>A qualifying edge was found and the window is aligned to it.</summary>
    Triggered,
    /// <summary>Auto mode without an edge; the record centre is shown.</summary>
    AutoFreeRun,
    /// <summary>Normal mode without an edge; the frame should not replace the display.</summary>
    Waiting
}

/// <summary>Result of applying view settings to one captured frame.</summary>
public sealed record ScopeView(
    AcquisitionFrame Frame,
    ScopeViewSettings Settings,
    int WindowStart,
    int WindowLength,
    int? TriggerIndex,
    TriggerState TriggerState,
    SignalMeasurements Channel1,
    SignalMeasurements Channel2,
    IReadOnlyList<FrequencyBin> Spectrum,
    FrequencyBin? SpectrumPeak)
{
    public double SampleRate => Frame.SampleRate;

    /// <summary>Time of the first visible sample relative to the trigger point.</summary>
    public double WindowStartTimeSeconds =>
        (WindowStart - (TriggerIndex ?? WindowStart + (WindowLength - 1) * Settings.TriggerPositionPercent / 100.0)) / SampleRate;

    /// <summary>Effective visible duration (can be shorter than requested for short records).</summary>
    public double WindowDurationSeconds => WindowLength > 1 ? (WindowLength - 1) / SampleRate : 0;

    public double TimeOfSample(int index) => WindowStartTimeSeconds + (index - WindowStart) / SampleRate;

    public IReadOnlyList<double> Samples(TriggerSource channel) => ScopeProcessor.SourceSamples(Frame, channel);
}

public static class ScopeProcessor
{
    /// <summary>Highest spectrum frequency shown, expressed in multiples of 1 / (time/div).</summary>
    public const double SpectrumSpanDivisionsFactor = 50;

    /// <summary>Upper bound for the spectrum FFT length to keep live updates responsive.</summary>
    public const int MaxFftLength = 1 << 19;

    /// <summary>Hysteresis floor for ADC data: idle-channel noise of ±1 code is not a signal.</summary>
    public const double AdcMinimumHysteresis = 1.5;

    public static IReadOnlyList<double> SourceSamples(AcquisitionFrame frame, TriggerSource source) =>
        source == TriggerSource.Channel2 ? frame.Channel2 : frame.Channel1;

    /// <param name="previous">
    /// Earlier view of the same frame. Its spectrum is reused when source, window function
    /// and time base are unchanged, so slider drags do not recompute a 512k FFT.
    /// </param>
    public static ScopeView Process(AcquisitionFrame frame, ScopeViewSettings settings, ScopeView? previous = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        settings = (settings ?? new()).Normalize();

        var count = frame.SampleCount;
        var rate = frame.SampleRate;

        if (count < 2 || !double.IsFinite(rate) || rate <= 0)
        {
            return new ScopeView(frame, settings, 0, count, null, TriggerState.AutoFreeRun,
                SignalMeasurements.Empty, SignalMeasurements.Empty, Array.Empty<FrequencyBin>(), null);
        }

        var windowLength = (int)Math.Clamp(Math.Round(settings.WindowDurationSeconds * rate) + 1, 2, count);
        var preTrigger = (int)Math.Round((windowLength - 1) * settings.TriggerPositionPercent / 100.0);
        var postTrigger = windowLength - 1 - preTrigger;

        int? triggerIndex = null;
        if (settings.TriggerSource is TriggerSource.Channel1 or TriggerSource.Channel2)
        {
            var source = SourceSamples(frame, settings.TriggerSource);
            var peakToPeak = SignalAnalyzer.Measure(source, 0, count, 0).PeakToPeak;
            var hysteresis = peakToPeak * settings.TriggerHysteresisFraction;

            // Prefer an edge near the record centre: the instrument's forced trigger sits
            // at 50 % and edges there have pre- and post-trigger data in both directions.
            var searchStart = Math.Max(preTrigger, count / 2 - windowLength / 2 + preTrigger);
            var searchEnd = count - postTrigger;
            triggerIndex = SoftwareTrigger.FindEdge(source, settings.TriggerLevel, settings.TriggerSlope, hysteresis, searchStart, searchEnd)
                ?? SoftwareTrigger.FindEdge(source, settings.TriggerLevel, settings.TriggerSlope, hysteresis, preTrigger, Math.Min(searchStart + 1, searchEnd));
        }

        TriggerState state;
        int windowStart;
        if (triggerIndex is { } index)
        {
            state = TriggerState.Triggered;
            windowStart = index - preTrigger;
        }
        else
        {
            state = settings.TriggerMode == SoftwareTriggerMode.Normal ? TriggerState.Waiting : TriggerState.AutoFreeRun;
            windowStart = (count - windowLength) / 2;
        }

        windowStart = Math.Clamp(windowStart, 0, count - windowLength);

        var floor = frame.SampleDomain == SampleDomain.AdcCounts ? AdcMinimumHysteresis : 0;
        var ch1 = SignalAnalyzer.Measure(frame.Channel1, windowStart, windowLength, rate, floor);
        var ch2 = SignalAnalyzer.Measure(frame.Channel2, windowStart, windowLength, rate, floor);

        IReadOnlyList<FrequencyBin> spectrum = Array.Empty<FrequencyBin>();
        FrequencyBin? peak = null;
        if (settings.ShowSpectrum)
        {
            if (previous is { } p &&
                ReferenceEquals(p.Frame, frame) &&
                p.Settings.ShowSpectrum &&
                p.Settings.TriggerSource == settings.TriggerSource &&
                p.Settings.FftWindow == settings.FftWindow &&
                p.Settings.TimePerDivisionSeconds == settings.TimePerDivisionSeconds)
            {
                (spectrum, peak) = (p.Spectrum, p.SpectrumPeak);
            }
            else
            {
                (spectrum, peak) = ComputeSpectrum(SourceSamples(frame, settings.TriggerSource), count, rate, settings);
            }
        }

        return new ScopeView(frame, settings, windowStart, windowLength, triggerIndex, state, ch1, ch2, spectrum, peak);
    }

    /// <summary>
    /// FFT over (up to) the whole record for resolution, limited to a display span
    /// derived from the time base. Mean is removed so the ADC offset does not dominate.
    /// </summary>
    private static (IReadOnlyList<FrequencyBin>, FrequencyBin?) ComputeSpectrum(
        IReadOnlyList<double> samples,
        int count,
        double rate,
        ScopeViewSettings settings)
    {
        var length = Math.Min(count, MaxFftLength);
        var start = (count - length) / 2;
        var segment = new double[length];
        for (var i = 0; i < length; i++)
            segment[i] = samples[start + i];

        var bins = FftAnalyzer.Analyze(segment, rate, settings.FftWindow, removeMean: true);
        if (bins.Count == 0)
            return (bins, null);

        var maxFrequency = Math.Min(rate / 2, SpectrumSpanDivisionsFactor / settings.TimePerDivisionSeconds);
        var visible = bins.TakeWhile(b => b.FrequencyHz <= maxFrequency).ToArray();
        var peak = visible.Skip(1).MaxBy(b => b.Amplitude);
        return (visible, peak);
    }
}
