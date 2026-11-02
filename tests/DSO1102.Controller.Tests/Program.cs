using System.Diagnostics;
using System.Text.Json;
using DSO1102.Controller.Core.Models;
using DSO1102.Controller.Core.SignalAnalysis;
using DSO1102.Controller.Hardware;

// The same executable is a deterministic child process for real pipe/lifecycle tests.
if (args.FirstOrDefault() == "server")
{
    var mode = File.ReadAllText(args[2]);
    while (Console.ReadLine() is { } command && command != "quit")
    {
        if (mode == "hang") { await Task.Delay(Timeout.Infinite); }
        if (mode == "exit") return;
        if (mode == "invalid") { Console.WriteLine("not-json"); continue; }
        if (mode == "array") { Console.WriteLine("[]"); continue; }
        if (mode == "frame" && command == "frame-1ms-adc")
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
