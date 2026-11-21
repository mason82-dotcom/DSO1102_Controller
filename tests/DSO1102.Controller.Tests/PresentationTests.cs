using System.Globalization;
using DSO1102.Controller.Core.Models;
using DSO1102.Controller.Core.Presentation;
using DSO1102.Controller.Core.Scope;
using DSO1102.Controller.Core.SignalAnalysis;
using static DSO1102.Controller.Tests.TestKit;

namespace DSO1102.Controller.Tests;

/// <summary>User-facing formatting, cursor read-out and the traced analog code table.</summary>
internal static class PresentationTests
{
    public static Task Run()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            Formatting();
            Cursors();
            AnalogTable();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        return Task.CompletedTask;
    }

    private static void Formatting()
    {
        Check(DisplayFormat.Value(1.5, SampleDomain.Volts) == "1,5 V", "volts with UI culture");
        Check(DisplayFormat.Value(0.25, SampleDomain.Volts) == "250 mV", "millivolts below 1 V");
        Check(DisplayFormat.Value(0.0, SampleDomain.Volts) == "0 V", "zero volts");
        Check(DisplayFormat.Value(127.5, SampleDomain.AdcCounts) == "127,5 ADC", "ADC codes");
        Check(DisplayFormat.Value(double.NaN, SampleDomain.Volts) == DisplayFormat.Missing, "non-finite shows dash");
        Check(DisplayFormat.Value((double?)null, SampleDomain.Volts) == DisplayFormat.Missing, "null shows dash");
        Check(DisplayFormat.Rate(5_000_000) == "5 MS/s" && DisplayFormat.Rate(2_500) == "2,5 kS/s", "sample rates");

        var empty = DisplayFormat.Readouts(null, SampleDomain.Volts);
        Check(empty.Length == DisplayFormat.ReadoutNames.Count && empty.All(t => t == DisplayFormat.Missing), "empty readouts");

        var m = new SignalMeasurements(-1, 1, 2, 0, 0.707, 1000, 0.001) { DutyCycle = 0.25, AcRms = 0.707 };
        var texts = DisplayFormat.Readouts(m, SampleDomain.Volts);
        Check(texts[0] == "1,000 kHz" && texts[1] == "1 ms" && texts[2] == "25,0 %" && texts[3] == "2 V", "readouts in name order");
        Check(DisplayFormat.Readouts(m with { DutyCycle = double.NaN }, SampleDomain.Volts)[2] == DisplayFormat.Missing, "unknown duty cycle");

        var nominal = new AcquisitionFrame(DateTimeOffset.UnixEpoch, 1, [0.0, 1.0], [0.0, 1.0]) { IsAmplitudeCalibrated = false, IsNominalScale = true };
        Check(DisplayFormat.AmplitudeKind(nominal).StartsWith("nominelle Volt", StringComparison.Ordinal), "nominal volts labelled");
        Check(DisplayFormat.AmplitudeKind(nominal with { IsNominalScale = false, SampleDomain = SampleDomain.AdcCounts }) == "unkalibrierte ADC-Werte",
            "ADC frames labelled uncalibrated");
    }

    private static void Cursors()
    {
        const double rate = 1_000_000;
        var ramp = Enumerable.Range(0, 100_000).Select(i => (double)i).ToArray();
        var frame = new AcquisitionFrame(DateTimeOffset.UnixEpoch, rate, ramp, ramp);
        var view = ScopeProcessor.Process(frame, new ScopeViewSettings { TriggerLevel = 50_000, TimePerDivisionSeconds = 1e-3 });

        Check(view.TriggerIndex is { } t && CursorReadout.ValueAt(view, ramp, 0) == ramp[t], "cursor at t=0 reads trigger sample");
        Check(CursorReadout.ValueAt(view, ramp, 1) is null, "cursor outside record reads nothing");
        Near(CursorReadout.TimeAtFraction(view, 0.5), view.WindowStartTimeSeconds + view.WindowDurationSeconds / 2, 1e-15, "fraction to time");
        Near(CursorReadout.Move(view, 0, 1), view.WindowDurationSeconds / 10, 1e-15, "move by one division");
        Near(CursorReadout.Move(view, 0, 100), view.WindowStartTimeSeconds + view.WindowDurationSeconds, 1e-15, "move clamps to window");

        Check(CursorReadout.Describe(null, 0, 0) == CursorReadout.Hint, "hint without view");
        Check(CursorReadout.Describe(view, null, null) == CursorReadout.Hint, "hint without cursors");
        var text = CursorReadout.Describe(view, 0, 1e-3);
        Check(text.Contains("Δt 1 ms", StringComparison.Ordinal) && text.Contains("1/Δt 1,000 kHz", StringComparison.Ordinal), "delta time and frequency");
        Check(text.Contains("ΔCH1 1000 V", StringComparison.Ordinal), "delta value between cursors");
    }

    private static void AnalogTable()
    {
        Check(AnalogFrontEnd.SupportedVoltsPerDivision.SequenceEqual([0.5, 1.0, 2.0]), "only traced V/div offered");
        Check(AnalogFrontEnd.RangeCode(0.5) == 5 && AnalogFrontEnd.RangeCode(1.0) == 6 && AnalogFrontEnd.RangeCode(2.0) == 7, "traced range codes");
        Reject(() => AnalogFrontEnd.RangeCode(0.2), "untraced V/div rejected");
        Check(Enumerable.Range(0, 10).Where(AnalogFrontEnd.IsTracedRangeCode).SequenceEqual([5, 6, 7]), "traced range code set");
        Check(AnalogFrontEnd.CouplingCode(ChannelCoupling.AC) == 1 && AnalogFrontEnd.CouplingCode(ChannelCoupling.Ground) == 0, "GND uses DC relay state");
        Check(AnalogFrontEnd.RangeLabel(5) == "500 mV/div" && AnalogFrontEnd.CouplingLabel(1) == "AC", "labels");
    }
}
