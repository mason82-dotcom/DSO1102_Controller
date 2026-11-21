using System.Diagnostics;
using System.Text.Json;
using DSO1102.Controller.Core.Models;
using DSO1102.Controller.Core.Presentation;
using DSO1102.Controller.Core.Scope;
using DSO1102.Controller.Core.SignalAnalysis;
using DSO1102.Controller.Hardware;
using static DSO1102.Controller.Tests.TestKit;

namespace DSO1102.Controller.Tests;

/// <summary>FFT scaling and basic measurements on analytic signals.</summary>
internal static class DspTests
{
    public static Task Run()
    {
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
        return Task.CompletedTask;
    }
}
