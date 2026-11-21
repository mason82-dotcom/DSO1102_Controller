using System.Diagnostics;
using System.Text.Json;
using DSO1102.Controller.Core.Models;
using DSO1102.Controller.Core.Presentation;
using DSO1102.Controller.Core.Scope;
using DSO1102.Controller.Core.SignalAnalysis;
using DSO1102.Controller.Hardware;
using static DSO1102.Controller.Tests.TestKit;

namespace DSO1102.Controller.Tests;

/// <summary>Device and bridge client against a fake bridge child process (this executable in server mode).</summary>
internal static class BridgeProcessTests
{
    public static async Task Run(string scenario)
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
            var log = File.ReadAllLines(scenario + ".log").Where(l => l.StartsWith("frame", StringComparison.Ordinal)).ToArray();
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
}
