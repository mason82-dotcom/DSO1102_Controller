using DSO1102.Controller.Core.Models;
using DSO1102.Controller.Core.Scope;
using DSO1102.Controller.Core.SignalAnalysis;
using DSO1102.Controller.Hardware;
using static DSO1102.Controller.Tests.TestKit;

namespace DSO1102.Controller.Tests;

/// <summary>Software trigger, window, measurements, spectrum, CSV, settings and simulator.</summary>
internal static class ScopeTests
{
    public static async Task Run()
    {
        const double rate = 5_000_000;
        const int count = 524_288;
        var rng = new Random(42);
        double[] Square(double freq, double phase, double noise) => Enumerable.Range(0, count)
            .Select(i => ((i / rate * freq + phase) % 1.0 < 0.5 ? 180.0 : 70.0) + noise * (rng.NextDouble() - 0.5)).ToArray();

        var square = Square(1000, 0.3, 6);
        var flat = Enumerable.Repeat(128.0, count).ToArray();
        var frame = new AcquisitionFrame(DateTimeOffset.UnixEpoch, rate, square, flat) { SampleDomain = SampleDomain.AdcCounts, IsAmplitudeCalibrated = false };

        var rising = SoftwareTrigger.FindEdge(square, 125, TriggerSlope.Rising, 10, 1, count);
        Check(rising is { } r && square[r] >= 125 && square[r - 1] < 125, "rising edge found at crossing");
        var falling = SoftwareTrigger.FindEdge(square, 125, TriggerSlope.Falling, 10, 1, count);
        Check(falling is { } f && square[f] <= 125 && square[f - 1] > 125, "falling edge found at crossing");
        Check(SoftwareTrigger.FindEdge(flat, 125, TriggerSlope.Rising, 1, 1, count) is null, "flat signal has no edge");
        Check(SoftwareTrigger.FindEdge(square, 300, TriggerSlope.Rising, 1, 1, count) is null, "level above signal has no edge");
        var chatter = new double[] { 0, 0, 9.9, 10.1, 9.9, 10.1, 20, 20, 0, 20 };
        Check(SoftwareTrigger.FindEdge(chatter, 10, TriggerSlope.Rising, 5, 4, chatter.Length) == 9,
            "hysteresis ignores chatter until re-armed below band");

        foreach (var position in new[] { 0.0, 10.0, 50.0, 90.0, 100.0 })
        {
            var settings = new ScopeViewSettings { TimePerDivisionSeconds = 1e-3, TriggerLevel = 125, TriggerPositionPercent = position };
            var view = ScopeProcessor.Process(frame, settings);
            Check(view.TriggerState == TriggerState.Triggered && view.TriggerIndex is not null, $"triggered at position {position}");
            Check(view.WindowLength == 50_001, $"10 div x 1 ms window at position {position}");
            var pre = (int)Math.Round((view.WindowLength - 1) * position / 100);
            Check(view.TriggerIndex!.Value - view.WindowStart == pre, $"trigger at requested screen position {position}");
            Near(view.TimeOfSample(view.TriggerIndex.Value), 0, 1e-15, $"time axis zero at trigger {position}");
        }

        var slow = ScopeProcessor.Process(frame, new ScopeViewSettings { TimePerDivisionSeconds = 10e-3, TriggerLevel = 125 });
        Check(slow.WindowLength <= count && slow.WindowStart >= 0 && slow.WindowStart + slow.WindowLength <= count, "10 ms/div window stays inside record");

        var auto = ScopeProcessor.Process(frame, new ScopeViewSettings { TriggerLevel = 250 });
        Check(auto.TriggerState == TriggerState.AutoFreeRun && auto.WindowStart == (count - auto.WindowLength) / 2, "auto without edge shows record centre");
        var normal = ScopeProcessor.Process(frame, new ScopeViewSettings { TriggerLevel = 250, TriggerMode = SoftwareTriggerMode.Normal });
        Check(normal.TriggerState == TriggerState.Waiting, "normal without edge waits");
        var ch2Trigger = ScopeProcessor.Process(frame, new ScopeViewSettings { TriggerLevel = 125, TriggerSource = TriggerSource.Channel2 });
        Check(ch2Trigger.TriggerState == TriggerState.AutoFreeRun, "trigger source selects CH2");

        var triggered = ScopeProcessor.Process(frame, new ScopeViewSettings { TimePerDivisionSeconds = 2e-3, TriggerLevel = 125 });
        Near(triggered.Channel1.FrequencyHz, 1000, 0.5, "noisy square frequency in window");
        Near(triggered.Channel1.DutyCycle, 0.5, 0.01, "square duty cycle");
        Near(triggered.Channel1.PeakToPeak, 116, 6.5, "square peak-to-peak");
        Check(triggered.Channel2.FrequencyHz == 0 && triggered.Channel2.PeakToPeak == 0, "flat channel has no frequency");
        Check(triggered.SpectrumPeak is { } peak && Math.Abs(peak.FrequencyHz - 1000) <= 2 * rate / ScopeProcessor.MaxFftLength, "spectrum peak at 1 kHz");
        Check(triggered.Spectrum[^1].FrequencyHz <= ScopeProcessor.SpectrumSpanDivisionsFactor / 2e-3 + 1e-9, "spectrum span follows time base");

        var noisySine = Enumerable.Range(0, 200_000).Select(i => Math.Sin(2 * Math.PI * 50 * i / 100_000.0) + 0.2 * (rng.NextDouble() - 0.5)).ToArray();
        Near(SignalAnalyzer.Measure(noisySine, 100_000).FrequencyHz, 50, 0.1, "hysteresis frequency on noisy sine");
        var part = SignalAnalyzer.Measure(square, 1000, 20_000, rate);
        Check(part.Minimum >= 64 && part.Maximum <= 186, "range measurement uses only the range");
        Reject(() => SignalAnalyzer.Measure(square, count - 10, 20, rate), "measurement range outside record rejected");

        var windowEnvelope = WaveformEnvelope.Create(square, 1000, 5000, 100);
        Check(windowEnvelope.All(p => p.SampleIndex >= 1000 && p.SampleIndex < 6000), "range envelope keeps absolute indices");

        Check(TimeBaseSteps.FittingRecord(count, rate).Contains(10e-3), "full record fits 10 ms/div");
        Check(TimeBaseSteps.FittingRecord(10_240, rate).Max() == 200e-6, "short record limits time base");
        Check(TimeBaseSteps.Format(5e-6) == "5 µs/div" && TimeBaseSteps.Format(2e-3) == "2 ms/div", "time base labels");

        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var csv = new StringWriter();
            WaveformCsvExporter.Write(csv, triggered, visibleWindowOnly: true);
            var lines = csv.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var header = Array.FindIndex(lines, l => l.StartsWith("time_s,", StringComparison.Ordinal));
            Check(lines[header].Trim() == "time_s,ch1_ADC,ch2_ADC", "csv header with unit");
            Check(lines.Length - header - 1 == triggered.WindowLength, "csv contains visible window");
            Check(lines[header + 1].Split(',').Length == 3 && !lines[header + 1].Contains(';'), "csv uses invariant separators");
            Check(lines.Any(l => l.StartsWith("# sample_rate_hz=5000000", StringComparison.Ordinal)), "csv sample rate is invariant");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }

        var settingsFile = Path.Combine(Path.GetTempPath(), $"dso-settings-{Guid.NewGuid():N}.json");
        try
        {
            var profile = new ScopeSettingsProfile().With(SampleDomain.AdcCounts,
                new ScopeViewSettings { TimePerDivisionSeconds = 2e-4, TriggerSlope = TriggerSlope.Falling, TriggerSource = TriggerSource.Channel2 });
            ScopeSettingsStore.Save(settingsFile, profile);
            var loaded = ScopeSettingsStore.Load(settingsFile).For(SampleDomain.AdcCounts);
            Check(loaded.TimePerDivisionSeconds == 2e-4 && loaded.TriggerSlope == TriggerSlope.Falling && loaded.TriggerSource == TriggerSource.Channel2,
                "settings round trip");
            File.WriteAllText(settingsFile, "{ not json");
            Check(ScopeSettingsStore.Load(settingsFile).For(SampleDomain.AdcCounts).TriggerLevel == 128, "damaged settings fall back to defaults");
            File.WriteAllText(settingsFile, "{\"AdcCounts\":{\"TimePerDivisionSeconds\":-1,\"TriggerPositionPercent\":500}}");
            var repaired = ScopeSettingsStore.Load(settingsFile).For(SampleDomain.AdcCounts);
            Check(repaired.TimePerDivisionSeconds > 0 && repaired.TriggerPositionPercent == 100, "invalid settings are normalised");
        }
        finally { File.Delete(settingsFile); }

        var idle = Enumerable.Range(0, count).Select(_ => 127.0 + rng.Next(3)).ToArray();
        var idleView = ScopeProcessor.Process(frame with { Channel2 = idle }, new ScopeViewSettings { TriggerLevel = 125 });
        Check(idleView.Channel2.FrequencyHz == 0 && double.IsNaN(idleView.Channel2.DutyCycle), "idle ADC noise is not a frequency");
        Check(ScopeProcessor.Process(frame, new ScopeViewSettings { TimePerDivisionSeconds = double.NaN, TriggerLevel = 125 }).WindowLength == 50_001,
            "NaN time base falls back to 1 ms/div");
        var cached = ScopeProcessor.Process(frame, new ScopeViewSettings { TriggerLevel = 140, TimePerDivisionSeconds = 2e-3 }, triggered);
        Check(ReferenceEquals(cached.Spectrum, triggered.Spectrum), "spectrum reused when only trigger level changes");
        var recomputed = ScopeProcessor.Process(frame, new ScopeViewSettings { TriggerLevel = 125, FftWindow = WindowFunction.Blackman, TimePerDivisionSeconds = 2e-3 }, triggered);
        Check(!ReferenceEquals(recomputed.Spectrum, triggered.Spectrum), "spectrum recomputed for new window function");
        var free = ScopeProcessor.Process(frame, new ScopeViewSettings { TriggerLevel = 250, TriggerPositionPercent = 100 });
        Near(free.TimeOfSample(free.WindowStart + free.WindowLength - 1), 0, 1e-15, "free-run time zero matches trigger marker");
        var partialFile = Path.Combine(Path.GetTempPath(), $"dso-partial-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(partialFile, "{\"Volts\":{\"TimePerDivisionSeconds\":0.002,\"Channel1\":{\"Visible\":false}}}");
            var partial = ScopeSettingsStore.Load(partialFile).For(SampleDomain.Volts);
            Check(partial.TimePerDivisionSeconds == 0.002 && partial.TriggerLevel == 1.0 && partial.Channel1.UnitsPerDivision == 0.5 && !partial.Channel1.Visible,
                "partial volt settings keep volt defaults");
        }
        finally { File.Delete(partialFile); }

        Near(NominalAdcScale.ToVolts(160, 1.0, 1), 1.0, 1e-12, "nominal 32 codes per division");
        await using (var analogSim = new SimulatedDsoDevice(seed: 3))
        {
            await analogSim.ConnectAsync();
            await analogSim.ApplySettingsAsync(new DsoSettings
            {
                SampleRate = rate, RecordLength = 100_000, ExperimentalAnalogControl = true,
                Channel1Coupling = ChannelCoupling.AC, Channel2Coupling = ChannelCoupling.Ground, Channel1ProbeFactor = 10
            });
            var simFrame = await analogSim.AcquireAsync();
            Near(simFrame.Channel1.Average(), 0, 1e-9, "simulator AC removes DC");
            Check(simFrame.Channel1.Max() - simFrame.Channel1.Min() > 15, "simulator probe factor scales amplitude");
            Check(simFrame.Channel2.All(v => v == 0), "simulator GND flat");
        }

        await using var simulator = new SimulatedDsoDevice(seed: 7);
        await simulator.ConnectAsync();
        await simulator.ApplySettingsAsync(new DsoSettings { SampleRate = rate, RecordLength = 262_144 });
        var simulated = await simulator.AcquireAsync();
        var simulatedView = ScopeProcessor.Process(simulated, ScopeSettingsProfile.DefaultsFor(SampleDomain.Volts));
        Check(simulated.SampleDomain == SampleDomain.Volts && simulated.SampleCount == 262_144, "simulator produces long volt records");
        Check(simulatedView.TriggerState == TriggerState.Triggered, "simulator square triggers with volt defaults");
        Near(simulatedView.Channel1.FrequencyHz, SimulatedDsoDevice.Channel1FrequencyHz, 1, "simulator CH1 frequency");
        Near(simulatedView.Channel2.FrequencyHz, SimulatedDsoDevice.Channel2FrequencyHz, 2, "simulator CH2 frequency");
    }
}
