using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using DSO1102.Controller.Core.Models;
using DSO1102.Controller.Core.Scope;

namespace DSO1102.Controller.App.Rendering;

/// <summary>Draws the magnitude spectrum in dB over a linear frequency axis.</summary>
internal static class SpectrumPlot
{
    private const int DbDivisions = 8;
    private const double DbPerDivision = 10;

    public static void Draw(Canvas canvas, ScopeView? view, bool visible)
    {
        canvas.Children.Clear();
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width < 20 || height < 20 || !visible) return;

        PlotStyle.DrawGrid(canvas, width, height, 10, DbDivisions);

        if (view is null || view.Spectrum.Count < 2)
        {
            PlotStyle.AddLabel(canvas, "Spektrum erscheint nach der ersten Aufnahme.", 8, 6, PlotStyle.LabelBrush);
            return;
        }

        var bins = view.Spectrum;
        var maxFrequency = bins[^1].FrequencyHz;
        var top = Math.Ceiling(Db(bins.Skip(1).Max(b => b.Amplitude)) / DbPerDivision) * DbPerDivision;
        var bottom = top - DbPerDivision * DbDivisions;

        var trace = new Polyline
        {
            Stroke = view.Settings.TriggerSource == TriggerSource.Channel2 ? PlotStyle.Ch2Brush : PlotStyle.Ch1Brush,
            StrokeThickness = 1.1
        };

        // Max-hold per pixel column so narrow peaks survive the reduction; DC (bin 0) is skipped.
        var buckets = Math.Max(1, (int)width);
        var index = 1;
        for (var bucket = 0; bucket < buckets && index < bins.Count; bucket++)
        {
            var bucketEnd = maxFrequency * (bucket + 1) / buckets;
            var max = double.NegativeInfinity;
            while (index < bins.Count && bins[index].FrequencyHz <= bucketEnd)
                max = Math.Max(max, bins[index++].Amplitude);
            if (double.IsNegativeInfinity(max)) continue;
            var y = Math.Clamp((top - Db(max)) / (top - bottom) * height, 0, height);
            trace.Points.Add(new Point(bucket + 0.5, y));
        }
        canvas.Children.Add(trace);

        var unit = view.Frame.SampleDomain == SampleDomain.Volts ? "dBV" : "dB re 1 ADC";
        PlotStyle.AddLabel(canvas, string.Create(CultureInfo.CurrentCulture, $"{top:0} {unit} · 10 dB/div"), 8, 6, PlotStyle.LabelBrush);
        PlotStyle.AddLabel(canvas, "0 Hz", 8, height - 22, PlotStyle.LabelBrush);
        PlotStyle.AddLabel(canvas, TimeBaseSteps.FormatFrequency(maxFrequency), width - 100, height - 22, PlotStyle.LabelBrush);

        if (view.SpectrumPeak is { } peak)
        {
            var x = peak.FrequencyHz / maxFrequency * width;
            PlotStyle.AddLabel(canvas, $"▼ {TimeBaseSteps.FormatFrequency(peak.FrequencyHz)}", Math.Clamp(x - 6, 0, width - 130), 26, PlotStyle.LabelBrush);
        }
    }

    private static double Db(double amplitude) => 20 * Math.Log10(Math.Max(amplitude, 1e-12));
}
