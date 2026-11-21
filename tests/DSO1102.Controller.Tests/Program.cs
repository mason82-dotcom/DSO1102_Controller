using System.Diagnostics;
using System.Text.Json;
using DSO1102.Controller.Core.Models;
using DSO1102.Controller.Core.Scope;
using DSO1102.Controller.Core.SignalAnalysis;
using DSO1102.Controller.Hardware;

// The same executable is a deterministic child process for real pipe/lifecycle tests.
if (args.FirstOrDefault() == "server")
{
    var mode = File.ReadAllText(args[2]);
    while (Console.ReadLine() is { } command && command != "quit")
    {
        File.AppendAllText(args[2] + ".log", command + "\n");
        if (mode == "hang") { await Task.Delay(Timeout.Infinite); }
        if (mode == "exit") return;
        if (mode == "invalid") { Console.WriteLine("not-json"); continue; }
        if (mode == "array") { Console.WriteLine("[]"); continue; }
        if (mode == "frame" && (command == "frame-1ms-adc" || command.StartsWith("frame-1ms-analog ")))
        {
            var bytes = new byte[10240 * 2];
            for (var i = 0; i < bytes.Length; i += 2) bytes[i] = 128;
            bytes[19000] = 255;
            Console.WriteLine(JsonSerializer.Serialize(new {
                ok = true, command, profile = new { decodedOutputReferenceRateHz = 5_000_000 },
                capture = new { adcPayload = new { encoding = "u16le-base64", sampleCountPerChannel = 10240,
                    channel1 = Convert.ToBase64String(bytes), channel2 = Convert.ToBase64String(bytes) } }
            }));
            continue;
        }
        if (mode == "frame" && command == "probe")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, command,
                devices = new[] { new { present = true, fpgaVersion = 1 } } }));
            continue;
        }
        if (mode == "slow") await Task.Delay(50);
        Console.WriteLine(JsonSerializer.Serialize(new { ok = mode != "fail", command = mode == "mismatch" ? "wrong" : command }));
    }
    return;
}

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}
void Near(double actual, double expected, double tolerance, string name) =>
    Check(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance, $"{name}: {actual:g6}");
void Reject(Action action, string name)
{
    try { action(); } catch (Exception) { Check(true, name); return; }
    throw new Exception($"Expected rejection: {name}");
}
async Task RejectAsync<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); } catch (T) { Check(true, name); return; }
    throw new Exception($"Expected {typeof(T).Name}: {name}");
}
string Encode(ushort[] values)
{
    var bytes = new byte[values.Length * 2];
    for (var i = 0; i < values.Length; i++)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
    return Convert.ToBase64String(bytes);
}

const int n = 4096;
var sine = Enumerable.Range(0, n).Select(i => 2 * Math.Sin(2 * Math.PI * 128 * i / n)).ToArray();
foreach (var window in Enum.GetValues<WindowFunction>())
{
    var bins = FftAnalyzer.Analyze(sine, n, window);
    Near(bins[128].Amplitude, 2, 1e-8, $"{window} coherent amplitude");
    Near(bins[128].FrequencyHz, 128, 0, $"{window} frequency");
    Near(FftAnalyzer.Analyze(Enumerable.Repeat(3.0, n).ToArray(), n, window)[0].Amplitude, 3, 1e-8, $"{window} DC not doubled");
}
var nyquist = Enumerable.Range(0, n).Select(i => i % 2 == 0 ? 2.0 : -2.0).ToArray();
Near(FftAnalyzer.Analyze(nyquist, n, WindowFunction.Rectangular)[n / 2].Amplitude, 2, 1e-8, "Nyquist not doubled");
Near(FftAnalyzer.Analyze(Enumerable.Repeat(128.0, n).ToArray(), n, removeMean: true).Max(x => x.Amplitude), 0, 0, "ADC DC removed before window");
Check(FftAnalyzer.Analyze(sine, double.NaN).Count == 0, "nonfinite sample rate");
Check(FftAnalyzer.Analyze(new[] { 1.0, -1.0 }, 2).All(x => double.IsFinite(x.Amplitude)), "two sample window remains finite");
Reject(() => FftAnalyzer.Analyze(new[] { double.NaN, 1.0 }, 2), "nonfinite input rejected");
var measurement = SignalAnalyzer.Measure(sine, n);
Near(measurement.Rms, Math.Sqrt(2), 1e-8, "sine RMS");
Near(measurement.FrequencyHz, 128, 1e-8, "sine frequency");

var raw = Enumerable.Repeat((ushort)128, 100).ToArray();
raw[0] = 65535; raw[99] = 300;
var decoded = AdcPayloadDecoder.Decode(Encode(raw), 100);
Check(decoded.ReplacedCount == 2 && decoded.Samples.All(v => v == 128), "sentinel replacement counted including edges");
raw = new ushort[524288]; raw[300000] = 255;
Check(AdcPayloadDecoder.Decode(Encode(raw), raw.Length).Samples[300000] == 255, "full record preserves distant spike and valid zeros");
raw = new ushort[100]; Array.Fill(raw, (ushort)65535, 94, 6);
Reject(() => AdcPayloadDecoder.Decode(Encode(raw), 100), "corrupt tail rejected");
Reject(() => AdcPayloadDecoder.Decode(Encode(new ushort[100]), 99), "extra bytes rejected");
Reject(() => AdcPayloadDecoder.Decode("???", 32), "malformed payload rejected");
Reject(() => AdcPayloadDecoder.Decode("", int.MaxValue), "unbounded count rejected");
var spike = new double[10000]; spike[6173] = 255; spike[6174] = -100;
var envelope = WaveformEnvelope.Create(spike, 100);
Check(envelope.Count <= 200 && envelope.Any(p => p.Value == 255) && envelope.Any(p => p.Value == -100), "display preserves extrema");
Check(envelope.Zip(envelope.Skip(1)).All(p => p.First.SampleIndex < p.Second.SampleIndex), "envelope preserves time ordering");
Check(WaveformEnvelope.Create(Array.Empty<double>(), 10).Count == 0, "empty display");
Check(!VendorSdkIdentity.Inspect(typeof(AcquisitionFrame).Assembly.Location).IsKnownBinary, "unknown PE identified without loading native code");
Reject(() => VendorSdkIdentity.RequireKnownBinary(typeof(AcquisitionFrame).Assembly.Location), "unknown DLL blocked");


// ---- software trigger, window, measurements (pure host logic, no hardware) ----
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
        var header = Array.FindIndex(lines, l => l.StartsWith("time_s,"));
        Check(lines[header].Trim() == "time_s,ch1_ADC,ch2_ADC", "csv header with unit");
        Check(lines.Length - header - 1 == triggered.WindowLength, "csv contains visible window");
        Check(lines[header + 1].Split(',').Length == 3 && !lines[header + 1].Contains(';'), "csv uses invariant separators");
        Check(lines.Any(l => l.StartsWith("# sample_rate_hz=5000000")), "csv sample rate is invariant");
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

if (args.Contains("--analysis-only"))
{
    Console.WriteLine($"PASS: {passed} analysis checks; process tests explicitly skipped.");
    return;
}

var scenario = Path.GetTempFileName();
try
{
    async Task SetMode(string mode) => await File.WriteAllTextAsync(scenario, mode);
    await SetMode("frame");
    await using (var device = new VoltcraftDso1102Device(Environment.ProcessPath!, scenario))
    {
        await device.ConnectAsync();
        await device.ApplySettingsAsync(new DsoSettings { RecordLength = 32 });
        var frame = await device.AcquireAsync();
        Check(frame.SampleCount == 10240 && frame.Channel1[9500] == 255, "device does not decimate measurement input");
        Near(frame.SampleRate, 5_000_000, 0, "device preserves source rate");
        Check(frame.SampleDomain == SampleDomain.AdcCounts && !frame.IsAmplitudeCalibrated, "hardware domain remains explicit");
        await RejectAsync<NotSupportedException>(() => device.ApplySettingsAsync(new DsoSettings { TimePerDivisionSeconds = double.NaN }), "NaN timebase rejected");
        await RejectAsync<NotSupportedException>(() => device.ApplySettingsAsync(new DsoSettings { TriggerMode = TriggerMode.Normal }), "unsupported trigger rejected");
    }
    await SetMode("frame");
    File.Delete(scenario + ".log");
    await using (var device = new VoltcraftDso1102Device(Environment.ProcessPath!, scenario))
    {
        await device.ConnectAsync();
        var analog = new DsoSettings
        {
            RecordLength = 32,
            ExperimentalAnalogControl = true,
            Channel1VoltsPerDivision = 2.0,
            Channel2VoltsPerDivision = 0.5,
            Channel1Coupling = ChannelCoupling.AC,
            Channel2Coupling = ChannelCoupling.Ground,
            Channel1ProbeFactor = 10
        };
        await device.ApplySettingsAsync(analog);
        var first = await device.AcquireAsync();
        var second = await device.AcquireAsync();
        await device.ApplySettingsAsync(analog with { Channel1VoltsPerDivision = 1.0 });
        var third = await device.AcquireAsync();
        await device.ApplySettingsAsync(analog with { ExperimentalAnalogControl = false });
        var plain = await device.AcquireAsync();
        var log = File.ReadAllLines(scenario + ".log").Where(l => l.StartsWith("frame")).ToArray();
        Check(log.SequenceEqual(new[] { "frame-1ms-analog 7 5 1 0", "frame-1ms-adc", "frame-1ms-analog 6 5 1 0", "frame-1ms-adc" }),
            "analog sequence only when front-end changes: " + string.Join(" | ", log));
        Check(first.SampleDomain == SampleDomain.Volts && first.IsNominalScale && !first.IsAmplitudeCalibrated, "analog frame is nominal volts");
        Near(first.Channel1[0], 0, 0, "code 128 is 0 V");
        Near(first.Channel1[9500], (255 - 128) / 32.0 * 2.0 * 10, 1e-12, "nominal scale with V/div and probe factor");
        Check(first.Channel2.All(v => v == 0), "software GND is a flat 0 V line");
        Near(third.Channel1[9500], (255 - 128) / 32.0 * 1.0 * 10, 1e-12, "changed V/div applied");
        Check(second.IsNominalScale && plain.SampleDomain == SampleDomain.AdcCounts && plain.Channel1[9500] == 255, "analog off returns ADC codes");
        await RejectAsync<NotSupportedException>(() => device.ApplySettingsAsync(analog with { Channel1VoltsPerDivision = 5.0 }), "untraced V/div rejected");
        await RejectAsync<ArgumentOutOfRangeException>(() => device.ApplySettingsAsync(analog with { Channel2ProbeFactor = 0 }), "invalid probe factor rejected");
    }
    File.Delete(scenario + ".log");
    await SetMode("valid");
    await using (var bridge = new BridgeProcessClient(Environment.ProcessPath!, scenario))
    {
        await RejectAsync<ArgumentException>(() => bridge.RunAsync("frame-1ms-analog 8 6 0 0", default), "untraced range code blocked in client");
        await RejectAsync<ArgumentException>(() => bridge.RunAsync("frame-1ms-analog 6 6 2 0", default), "GND/unknown coupling code blocked in client");
        await RejectAsync<ArgumentException>(() => bridge.RunAsync("frame-1ms-analog 6 6 0 0\nprobe", default), "analog command injection blocked");
    }
    await SetMode("slow");
    await using (var bridge = new BridgeProcessClient(Environment.ProcessPath!, scenario))
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => bridge.RunAsync("probe", default)));
        foreach (var response in responses) response.Dispose();
        Check(responses.Length == 4, "concurrent pipe commands serialized");
        await RejectAsync<ArgumentException>(() => bridge.RunAsync("quit\nprobe", default), "newline injection rejected");
        await bridge.DisposeAsync();
        await bridge.DisposeAsync();
        await RejectAsync<ObjectDisposedException>(() => bridge.RunAsync("probe", default), "disposed bridge cannot restart");
    }
    foreach (var mode in new[] { "invalid", "array", "mismatch", "fail", "exit" })
    {
        await SetMode(mode);
        await using var bridge = new BridgeProcessClient(Environment.ProcessPath!, scenario);
        await RejectAsync<InvalidOperationException>(() => bridge.RunAsync("probe", default), $"{mode} response rejected");
        await SetMode("valid");
        using var response = await bridge.RunAsync("probe", default);
        Check(response.RootElement.GetProperty("ok").GetBoolean(), $"fresh process after {mode}");
    }
    await SetMode("hang");
    await using (var bridge = new BridgeProcessClient(Environment.ProcessPath!, scenario, TimeSpan.FromMilliseconds(400)))
    {
        var clock = Stopwatch.StartNew();
        await RejectAsync<TimeoutException>(() => bridge.RunAsync("probe", default), "native hang bounded by timeout");
        Check(clock.Elapsed < TimeSpan.FromSeconds(5), "timeout finishes promptly");
        await SetMode("valid");
        using var response = await bridge.RunAsync("probe", default);
        Check(response.RootElement.GetProperty("ok").GetBoolean(), "fresh process after timeout");
    }
    await SetMode("hang");
    await using (var bridge = new BridgeProcessClient(Environment.ProcessPath!, scenario))
    {
        using var cancel = new CancellationTokenSource(200);
        await RejectAsync<OperationCanceledException>(() => bridge.RunAsync("probe", cancel.Token), "cancel interrupted pipe operation");
        await SetMode("valid");
        using var response = await bridge.RunAsync("probe", default);
        Check(response.RootElement.GetProperty("ok").GetBoolean(), "fresh process after cancel");
    }
}
finally { File.Delete(scenario); }
Console.WriteLine($"PASS: {passed} regression checks; no hardware calls.");
