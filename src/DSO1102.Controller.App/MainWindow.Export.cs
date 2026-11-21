using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DSO1102.Controller.Core.Scope;
using Microsoft.Win32;

namespace DSO1102.Controller.App;

// CSV and PNG export of the shown view.
public partial class MainWindow
{
    private async void ExportWindowCsv_Click(object sender, RoutedEventArgs e) => await ExportCsvAsync(visibleWindowOnly: true);

    private async void ExportRecordCsv_Click(object sender, RoutedEventArgs e) => await ExportCsvAsync(visibleWindowOnly: false);

    private async Task ExportCsvAsync(bool visibleWindowOnly)
    {
        var view = _view;
        if (view is null) return;

        var dialog = new SaveFileDialog
        {
            Title = visibleWindowOnly ? "Sichtbares Fenster als CSV speichern" : "Ganzen Record als CSV speichern",
            Filter = "CSV-Datei (*.csv)|*.csv",
            FileName = $"dso1102_{view.Frame.Timestamp:yyyyMMdd_HHmmss}{(visibleWindowOnly ? "_fenster" : "_record")}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var path = dialog.FileName;
            await Task.Run(() =>
            {
                using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
                WaveformCsvExporter.Write(writer, view, visibleWindowOnly);
            });
            StatusText.Text = $"CSV gespeichert: {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"CSV konnte nicht gespeichert werden: {ex.Message} — anderen Speicherort wählen.";
        }
    }

    private void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        var view = _view;
        if (view is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Bild als PNG speichern",
            Filter = "PNG-Bild (*.png)|*.png",
            FileName = $"dso1102_{view.Frame.Timestamp:yyyyMMdd_HHmmss}.png"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            const double scale = 2;
            var width = (int)Math.Ceiling(ScopeArea.ActualWidth * scale);
            var height = (int)Math.Ceiling(ScopeArea.ActualHeight * scale);
            var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(ScopeArea);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
            StatusText.Text = $"Bild gespeichert: {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Bild konnte nicht gespeichert werden: {ex.Message} — anderen Speicherort wählen.";
        }
    }
}
