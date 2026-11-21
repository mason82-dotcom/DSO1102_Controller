using System.Diagnostics;
using System.Text.Json;
using DSO1102.Controller.Core.Models;
using DSO1102.Controller.Core.Presentation;
using DSO1102.Controller.Core.Scope;
using DSO1102.Controller.Core.SignalAnalysis;
using DSO1102.Controller.Hardware;
using static DSO1102.Controller.Tests.TestKit;

namespace DSO1102.Controller.Tests;

/// <summary>ADC payload decoding, display envelope and vendor DLL identity.</summary>
internal static class PayloadTests
{
    public static Task Run()
    {
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
        return Task.CompletedTask;
    }
}
