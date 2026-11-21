using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DSO1102.Controller.App.Rendering;
using DSO1102.Controller.Core.Presentation;
using DSO1102.Controller.Core.Scope;
using DSO1102.Controller.Core.SignalAnalysis;

namespace DSO1102.Controller.App;

// Showing processed views: plots, measurement bar, status texts and cursors.
public partial class MainWindow
{
    private void ShowView(ScopeView view, bool fromReprocess = false)
    {
        var firstView = _view is null;
        _view = view;
        if (!fromReprocess) _reprocessVersion++;
        if (firstView)
        {
            // The step list depends on the record length; if the stored time base does
            // not fit, it is snapped and the view re-evaluated with the snapped value.
            var before = _settings.TimePerDivisionSeconds;
            RefreshTimeBaseItems();
            if (_settings.TimePerDivisionSeconds != before)
            {
                _profile = _profile.With(_domain, _settings);
                _ = ReprocessAsync();
            }
        }

        ShowReadouts(_ch1Readouts, view.Channel1);
        ShowReadouts(_ch2Readouts, view.Channel2);
        FftText.Text = DisplayFormat.SpectrumSummary(view);
        StatusText.Text = DisplayFormat.FrameStatus(view);

        UpdateButtons();
        RefreshCursors();
        SpectrumPlot.Draw(SpectrumCanvas, _view, _settings.ShowSpectrum);
    }

    private void ClearView()
    {
        _view = null;
        _reprocessVersion++;
        _cursorA = _cursorB = null;
        ShowReadouts(_ch1Readouts, null);
        ShowReadouts(_ch2Readouts, null);
        FftText.Text = DisplayFormat.Missing;
        UpdateButtons();
        CursorText.Text = CursorReadout.Hint;
        DrawPlots();
    }

    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPlots();

    private void DrawPlots()
    {
        ScopePlot.Draw(ScopeCanvas, _view, _cursorA, _cursorB);
        SpectrumPlot.Draw(SpectrumCanvas, _view, _settings.ShowSpectrum);
    }

    // ---------------------------------------------------------------- measurement bar

    private static TextBlock[] BuildReadouts(UniformGrid host)
    {
        host.Children.Clear();
        var names = DisplayFormat.ReadoutNames;
        var values = new TextBlock[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            cell.Children.Add(new TextBlock { Text = names[i], Style = (Style)host.FindResource("ReadoutLabel") });
            values[i] = new TextBlock
            {
                Text = DisplayFormat.Missing,
                Style = (Style)host.FindResource("ReadoutValue"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            cell.Children.Add(values[i]);
            host.Children.Add(cell);
        }
        return values;
    }

    private void ShowReadouts(TextBlock[] cells, SignalMeasurements? measurements)
    {
        var texts = DisplayFormat.Readouts(measurements, _view?.Frame.SampleDomain ?? _domain);
        for (var i = 0; i < cells.Length && i < texts.Length; i++)
            cells[i].Text = texts[i];
    }

    // ---------------------------------------------------------------- cursors

    private void ScopeCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => SetCursorFromMouse(e, isA: true);

    private void ScopeCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e) => SetCursorFromMouse(e, isA: false);

    private void SetCursorFromMouse(MouseButtonEventArgs e, bool isA)
    {
        if (_view is not { WindowLength: >= 2 } view) return;
        var time = CursorReadout.TimeAtFraction(view, ScopePlot.Fraction(ScopeCanvas, e.GetPosition(ScopeCanvas).X));
        SetCursor(isA, time);
        e.Handled = true;
    }

    /// <summary>Ctrl+1/Ctrl+2 place A/B at the trigger point; Ctrl(+Shift)+arrow moves B (A) by 0.1 div.</summary>
    private void PlaceCursorFromKeyboard(bool isA, double? deltaDivisions)
    {
        if (_view is not { } view) return;
        var current = isA ? _cursorA : _cursorB;
        var time = deltaDivisions is { } d ? CursorReadout.Move(view, current, d) : CursorReadout.Clamp(view, 0);
        SetCursor(isA, time);
    }

    private void SetCursor(bool isA, double time)
    {
        if (isA) _cursorA = time; else _cursorB = time;
        UpdateButtons();
        RefreshCursors();
    }

    private void ClearCursors_Click(object sender, RoutedEventArgs e) => ClearCursors();

    private void ClearCursors()
    {
        _cursorA = _cursorB = null;
        UpdateButtons();
        RefreshCursors();
    }

    /// <summary>Cursor text and the scope plot (which draws the cursor lines).</summary>
    private void RefreshCursors()
    {
        CursorText.Text = CursorReadout.Describe(_view, _cursorA, _cursorB);
        ScopePlot.Draw(ScopeCanvas, _view, _cursorA, _cursorB);
    }
}
