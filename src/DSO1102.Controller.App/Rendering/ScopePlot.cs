using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DSO1102.Controller.Core.Models;
using DSO1102.Controller.Core.Presentation;
using DSO1102.Controller.Core.Scope;
using DSO1102.Controller.Core.SignalAnalysis;

namespace DSO1102.Controller.App.Rendering;

/// <summary>Draws the time-domain view: grid, traces, trigger markers, cursors and axis labels.</summary>
internal static class ScopePlot
{
    public const string EmptyText = "Keine Aufnahme. Simulator oder DSO-1102 verbinden.";

    public static void Draw(Canvas canvas, ScopeView? view, double? cursorA, double? cursorB)
    {
        canvas.Children.Clear();
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width < 20 || height < 20) return;

        PlotStyle.DrawGrid(canvas, width, height, ScopeViewSettings.HorizontalDivisions, ScopeViewSettings.VerticalDivisions);

        if (view is null)
        {
            PlotStyle.AddLabel(canvas, EmptyText, width / 2 - 170, height / 2 - 10, PlotStyle.LabelBrush);
            return;
        }

        var s = view.Settings;
        if (s.Channel1.Visible) DrawTrace(canvas, view, view.Frame.Channel1, s.Channel1, PlotStyle.Ch1Brush, width, height);
        if (s.Channel2.Visible) DrawTrace(canvas, view, view.Frame.Channel2, s.Channel2, PlotStyle.Ch2Brush, width, height);

        DrawTriggerMarkers(canvas, view, width, height);
        DrawCursor(canvas, view, cursorA, "A", width, height);
        DrawCursor(canvas, view, cursorB, "B", width, height);
        DrawLabels(canvas, view, width, height);
    }

    /// <summary>Horizontal plot fraction (0..1) for a mouse x position.</summary>
    public static double Fraction(Canvas canvas, double x) =>
        canvas.ActualWidth < 2 ? 0 : Math.Clamp(x / canvas.ActualWidth, 0, 1);

    private static void DrawTrace(Canvas canvas, ScopeView view, IReadOnlyList<double> samples, ChannelView channel, Brush brush, double width, double height)
    {
        if (view.WindowLength < 2 || samples.Count < view.WindowStart + view.WindowLength) return;

        var points = WaveformEnvelope.Create(samples, view.WindowStart, view.WindowLength, Math.Max(1, (int)width));
        var trace = new Polyline { Stroke = brush, StrokeThickness = 1.3 };
        var scaleX = width / (view.WindowLength - 1.0);
        foreach (var point in points)
        {
            var y = Math.Clamp(ValueToY(point.Value, channel, height), -2, height + 2);
            trace.Points.Add(new Point((point.SampleIndex - view.WindowStart) * scaleX, y));
        }
        canvas.Children.Add(trace);

        // Centre reference marker on the right edge.
        var centerY = height / 2;
        canvas.Children.Add(new Polygon
        {
            Points = [new Point(width, centerY - 5), new Point(width - 8, centerY), new Point(width, centerY + 5)],
            Fill = brush,
            Opacity = 0.7
        });
    }

    private static void DrawTriggerMarkers(Canvas canvas, ScopeView view, double width, double height)
    {
        var s = view.Settings;
        var triggerX = width * s.TriggerPositionPercent / 100.0;
        canvas.Children.Add(new Line
        {
            X1 = triggerX, X2 = triggerX, Y1 = 0, Y2 = height,
            Stroke = PlotStyle.TriggerBrush, StrokeThickness = 1, StrokeDashArray = [4, 4],
            Opacity = view.TriggerState == TriggerState.Triggered ? 0.8 : 0.35
        });

        var sourceView = s.TriggerSource == TriggerSource.Channel2 ? s.Channel2 : s.Channel1;
        var levelY = ValueToY(s.TriggerLevel, sourceView, height);
        if (levelY >= 0 && levelY <= height)
        {
            canvas.Children.Add(new Line { X1 = 0, X2 = 14, Y1 = levelY, Y2 = levelY, Stroke = PlotStyle.TriggerBrush, StrokeThickness = 2 });
            PlotStyle.AddLabel(canvas, "T", 16, levelY - 9, PlotStyle.TriggerBrush);
        }
    }

    private static void DrawCursor(Canvas canvas, ScopeView view, double? time, string name, double width, double height)
    {
        if (time is not { } t || view.WindowDurationSeconds <= 0) return;
        var x = (t - view.WindowStartTimeSeconds) / view.WindowDurationSeconds * width;
        if (x < 0 || x > width) return;
        canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = height, Stroke = PlotStyle.CursorBrush, StrokeThickness = 1 });
        PlotStyle.AddLabel(canvas, name, Math.Min(x + 3, width - 16), 24, PlotStyle.CursorBrush);
    }

    private static void DrawLabels(Canvas canvas, ScopeView view, double width, double height)
    {
        var s = view.Settings;
        var domain = view.Frame.SampleDomain;
        var header = new StringBuilder();
        if (s.Channel1.Visible) header.Append("CH1 ").Append(DisplayFormat.Value(s.Channel1.UnitsPerDivision, domain)).Append("/div   ");
        if (s.Channel2.Visible) header.Append("CH2 ").Append(DisplayFormat.Value(s.Channel2.UnitsPerDivision, domain)).Append("/div");

        PlotStyle.AddLabel(canvas, header.ToString().TrimEnd(), 8, 6, PlotStyle.LabelBrush);
        PlotStyle.AddLabel(canvas, DisplayFormat.TimeAxis(view), 8, height - 24, PlotStyle.LabelBrush);
        PlotStyle.AddLabel(canvas, DisplayFormat.TriggerState(view), width - 170, 6, PlotStyle.TriggerBrush);

        if (!view.Frame.IsAmplitudeCalibrated)
        {
            var note = view.Frame.IsNominalScale ? "V nominell (unkalibriert)" : $"Einheit: {DisplayFormat.Unit(domain)} (unkalibriert)";
            PlotStyle.AddLabel(canvas, note, width - 220, height - 24, PlotStyle.LabelBrush);
        }
    }

    private static double ValueToY(double value, ChannelView channel, double height) =>
        height / 2 - (value - channel.CenterValue) / channel.UnitsPerDivision * (height / ScopeViewSettings.VerticalDivisions);
}
