using System.Globalization;
using DSO1102.Controller.Core.Models;
using DSO1102.Controller.Core.Scope;
using DSO1102.Controller.Core.SignalAnalysis;

namespace DSO1102.Controller.Core.Presentation;

/// <summary>User-facing (German) text for values, units and states. UI-free and testable.</summary>
public static class DisplayFormat
{
    public const string Missing = "—";

    /// <summary>UI culture used for numbers (current culture of the UI thread).</summary>
    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    public static IReadOnlyList<string> ReadoutNames { get; } =
        ["Frequenz", "Periode", "Tastgrad", "Spitze-Spitze", "Minimum", "Maximum", "Mittelwert", "RMS", "AC-RMS"];

    public static string Value(double value, SampleDomain domain)
    {
        if (!double.IsFinite(value))
            return Missing;

        if (domain != SampleDomain.Volts)
            return $"{value.ToString("0.#", Culture)} ADC";

        return Math.Abs(value) < 1 && value != 0
            ? $"{(value * 1000).ToString("0.#", Culture)} mV"
            : $"{value.ToString("0.###", Culture)} V";
    }

    public static string Value(double? value, SampleDomain domain) =>
        value is { } v ? Value(v, domain) : Missing;

    public static string Unit(SampleDomain domain) => domain == SampleDomain.Volts ? "V" : "ADC";

    public static string Rate(double samplesPerSecond)
    {
        return samplesPerSecond switch
        {
            >= 1e6 => string.Create(Culture, $"{samplesPerSecond / 1e6:0.###} MS/s"),
            >= 1e3 => string.Create(Culture, $"{samplesPerSecond / 1e3:0.###} kS/s"),
            _ => string.Create(Culture, $"{samplesPerSecond:0.#} S/s")
        };
    }

    public static string Source(TriggerSource source) => source switch
    {
        TriggerSource.Channel2 => "CH2",
        TriggerSource.External => "EXT",
        _ => "CH1"
    };

    public static string Slope(TriggerSlope slope) => slope == TriggerSlope.Falling ? "fallend" : "steigend";

    public static string Window(WindowFunction window) => window switch
    {
        WindowFunction.Rectangular => "Rechteck",
        WindowFunction.Hann => "Hann",
        WindowFunction.Hamming => "Hamming",
        WindowFunction.Blackman => "Blackman",
        _ => window.ToString()
    };

    /// <summary>Texts for the measurement bar, in the order of <see cref="ReadoutNames"/>.</summary>
    public static string[] Readouts(SignalMeasurements? m, SampleDomain domain)
    {
        if (m is null)
            return Enumerable.Repeat(Missing, ReadoutNames.Count).ToArray();

        return
        [
            TimeBaseSteps.FormatFrequency(m.FrequencyHz),
            m.PeriodSeconds > 0 ? TimeBaseSteps.FormatTime(m.PeriodSeconds) : Missing,
            double.IsFinite(m.DutyCycle) ? string.Create(Culture, $"{m.DutyCycle * 100:F1} %") : Missing,
            Value(m.PeakToPeak, domain),
            Value(m.Minimum, domain),
            Value(m.Maximum, domain),
            Value(m.Mean, domain),
            Value(m.Rms, domain),
            Value(m.AcRms, domain)
        ];
    }

    public static string TriggerState(ScopeView view) => view.TriggerState switch
    {
        Scope.TriggerState.Triggered => $"Trig {Source(view.Settings.TriggerSource)} {Slope(view.Settings.TriggerSlope)}",
        Scope.TriggerState.AutoFreeRun => "Auto (keine Flanke)",
        _ => "Keine Flanke"
    };

    public static string TriggerStateLong(ScopeView view) => view.TriggerState switch
    {
        Scope.TriggerState.Triggered => "getriggert",
        Scope.TriggerState.AutoFreeRun => "Auto ohne Flanke (Recordmitte)",
        _ => "keine Flanke"
    };

    /// <summary>Unit/calibration note: Volt, nominal volt or uncalibrated ADC codes.</summary>
    public static string AmplitudeKind(AcquisitionFrame frame) =>
        frame.IsNominalScale ? "nominelle Volt (unkalibriert)"
        : frame.IsAmplitudeCalibrated ? "Volt"
        : "unkalibrierte ADC-Werte";

    public static string TimeAxis(ScopeView view) =>
        $"{TimeBaseSteps.Format(view.WindowDurationSeconds / ScopeViewSettings.HorizontalDivisions)}   " +
        $"Fenster {TimeBaseSteps.FormatTime(view.WindowStartTimeSeconds)} … " +
        $"{TimeBaseSteps.FormatTime(view.WindowStartTimeSeconds + view.WindowDurationSeconds)}";

    public static string FrameStatus(ScopeView view)
    {
        var frame = view.Frame;
        var replaced = frame.ReplacedSamplesChannel1 + frame.ReplacedSamplesChannel2 > 0
            ? string.Create(Culture, $" — Ersatzwerte CH1/CH2: {frame.ReplacedSamplesChannel1}/{frame.ReplacedSamplesChannel2}")
            : string.Empty;

        return string.Create(Culture,
            $"Aufnahme {frame.Timestamp:HH:mm:ss.fff} — {frame.SampleCount:N0} Samples @ {Rate(frame.SampleRate)} — " +
            $"Fenster {TimeBaseSteps.FormatTime(view.WindowDurationSeconds)} — Trigger {TriggerStateLong(view)} — {AmplitudeKind(frame)}{replaced}");
    }

    public static string SpectrumSummary(ScopeView view)
    {
        if (view.SpectrumPeak is not { } peak)
            return Missing;

        var resolution = view.Spectrum.Count > 1 ? view.Spectrum[1].FrequencyHz - view.Spectrum[0].FrequencyHz : 0;
        return $"Spitze  {TimeBaseSteps.FormatFrequency(peak.FrequencyHz)}\n" +
               $"Ampl.   {Value(peak.Amplitude, view.Frame.SampleDomain)}\n" +
               $"Auflös. {TimeBaseSteps.FormatFrequency(resolution)}";
    }
}
