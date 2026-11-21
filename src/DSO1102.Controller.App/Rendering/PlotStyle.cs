using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DSO1102.Controller.App.Rendering;

/// <summary>Colours and primitive drawing helpers shared by the scope and spectrum plots.</summary>
internal static class PlotStyle
{
    // Data colours come from Themes/Theme.xaml; the fallbacks keep drawing usable without
    // the application resources (designer, tests).
    public static readonly Color Ch1 = ThemeColor("Ch1Color", Color.FromRgb(0xFF, 0xD8, 0x4D));
    public static readonly Color Ch2 = ThemeColor("Ch2Color", Color.FromRgb(0x4F, 0xD1, 0xFF));
    public static readonly Color Trigger = Color.FromRgb(0xFF, 0x9F, 0x43);
    public static readonly Color Cursor = Color.FromRgb(0xB7, 0x94, 0xF6);

    public static readonly Brush GridBrush = Frozen(Color.FromRgb(0x1A, 0x1E, 0x26));
    public static readonly Brush AxisBrush = Frozen(Color.FromRgb(0x2C, 0x32, 0x3D));
    public static readonly Brush LabelBrush = Frozen(ThemeColor("TextSecondaryColor", Color.FromRgb(0xA3, 0xAB, 0xB8)));
    public static readonly Brush TriggerBrush = Frozen(Trigger);
    public static readonly Brush CursorBrush = Frozen(Cursor);
    public static readonly Brush Ch1Brush = Frozen(Ch1);
    public static readonly Brush Ch2Brush = Frozen(Ch2);

    // Semi-opaque scope background keeps labels readable where traces pass underneath.
    private static readonly Brush LabelBackdrop = Frozen(Color.FromArgb(0xCC, 0x07, 0x09, 0x0C));
    private static readonly FontFamily MonoFamily = new("Cascadia Mono, Consolas");

    public static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static void DrawGrid(Canvas canvas, double width, double height, int columns, int rows)
    {
        for (var i = 1; i < columns; i++)
        {
            var x = width * i / columns;
            canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = height, Stroke = i == columns / 2 ? AxisBrush : GridBrush, StrokeThickness = 1 });
        }

        for (var i = 1; i < rows; i++)
        {
            var y = height * i / rows;
            canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, Stroke = i == rows / 2 ? AxisBrush : GridBrush, StrokeThickness = 1 });
        }
    }

    public static void AddLabel(Canvas canvas, string text, double x, double y, Brush brush)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontFamily = MonoFamily,
            FontSize = 12,
            Background = LabelBackdrop,
            Padding = new Thickness(4, 1, 4, 1)
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        canvas.Children.Add(label);
    }

    private static Color ThemeColor(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) is Color color ? color : fallback;
}
