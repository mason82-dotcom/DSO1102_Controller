using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using DSO1102.Controller.Core.Abstractions;
using DSO1102.Controller.Core.Models;
using DSO1102.Controller.Core.SignalAnalysis;
using DSO1102.Controller.Hardware;

namespace DSO1102.Controller.App;

public partial class MainWindow : Window
{
    private readonly DsoSettings _settings = new()
    {
        SampleRate = 100_000,
        RecordLength = 4096,
        TimePerDivisionSeconds = 0.001
    };
    private IDsoDevice? _device;
    private CancellationTokenSource? _runCancellation;
    private AcquisitionFrame? _lastFrame;

    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
    }

    private TaskCompletionSource? _operationCompletion;
    private bool _closing;
    private bool _closeReady;

    private async void ConnectSimulator_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(token => ConnectAsync(new SimulatedDsoDevice(), token));

    private async void ConnectHardware_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(token => ConnectAsync(new VoltcraftDso1102Device(), token));

    private async Task ConnectAsync(IDsoDevice device, CancellationToken token)
    {
        if (_device is not null)
            await _device.DisposeAsync();
        _device = device;
        ClearFrame();
        DeviceHeaderText.Text = "Verbindung wird aufgebaut …";
        await device.ConnectAsync(token);
        await device.ApplySettingsAsync(_settings, token);
        DeviceHeaderText.Text = $"{device.DeviceInfo.Name} — {device.DeviceInfo.Firmware}";
        await AcquireAndRenderAsync(token);
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_device?.IsConnected != true) return;
        await RunOperationAsync(async token =>
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await AcquireAndRenderAsync(token);
                await Task.Delay(40, token);
            }
        });
    }

    private async void Single_Click(object sender, RoutedEventArgs e)
    {
        if (_device?.IsConnected == true)
            await RunOperationAsync(AcquireAndRenderAsync);
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (_runCancellation is not null || _closing) return;
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runCancellation = cancellation;
        _operationCompletion = completion;
        UpdateButtons();
        StatusText.Text = "Erfassung / Verbindung läuft …";
        try
        {
            await action(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ClearFrame();
            StatusText.Text = "Vorgang abgebrochen.";
        }
        catch (Exception ex)
        {
            ClearFrame();
            StatusText.Text = $"Fehler: {ex.Message} — bitte erneut verbinden.";
            var device = _device;
            _device = null;
            DeviceHeaderText.Text = "Nicht verbunden";
            if (device is not null)
            {
                try { await device.DisposeAsync(); }
                catch (Exception cleanup) { StatusText.Text += $" Bereinigung: {cleanup.Message}"; }
            }
        }
        finally
        {
            _runCancellation = null;
            _operationCompletion = null;
            UpdateButtons();
            completion.TrySetResult();
        }
    }

    private void UpdateButtons()
    {
        var idle = _runCancellation is null && !_closing;
        ConnectSimulatorButton.IsEnabled = idle;
        ConnectHardwareButton.IsEnabled = idle;
        RunButton.IsEnabled = SingleButton.IsEnabled = idle && _device?.IsConnected == true;
        StopButton.IsEnabled = !idle && !_closing;
    }

    private void ClearFrame()
    {
        _lastFrame = null;
        Ch1MeasurementsText.Text = Ch2MeasurementsText.Text = FftText.Text = "—";
        DrawScope();
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopAcquisitionAsync();

    private async Task StopAcquisitionAsync()
    {
        var completion = _operationCompletion;
        _runCancellation?.Cancel();
        if (completion is not null) await completion.Task;
    }

    private async Task AcquireAndRenderAsync(CancellationToken cancellationToken)
    {
        if (_device is null)
            return;

        var frame = await _device.AcquireAsync(cancellationToken);
        var analysis = await Task.Run(() =>
        {
            var ch1 = SignalAnalyzer.Measure(frame.Channel1, frame.SampleRate);
            var ch2 = SignalAnalyzer.Measure(frame.Channel2, frame.SampleRate);
            var peak = FftAnalyzer.Analyze(frame.Channel1, frame.SampleRate, removeMean: true)
                .Skip(1).MaxBy(x => x.Amplitude);
            return (ch1, ch2, peak);
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _lastFrame = frame;
        var (ch1, ch2, peak) = analysis;

        Ch1MeasurementsText.Text = FormatMeasurements(ch1, _lastFrame.SampleDomain);
        Ch2MeasurementsText.Text = FormatMeasurements(ch2, _lastFrame.SampleDomain);
        var amplitudeUnit = _lastFrame.SampleDomain == SampleDomain.Volts ? "V" : "ADC";
        FftText.Text = peak is null ? "—" : $"Peak {FormatFrequency(peak.FrequencyHz)}\nAmp  {peak.Amplitude:F3} {amplitudeUnit}";
        var calibrationState = _lastFrame.IsAmplitudeCalibrated ? "kalibriert" : "unkalibrierte ADC-Domain";
        StatusText.Text = $"Capture {_lastFrame.Timestamp:HH:mm:ss.fff} — {_lastFrame.SampleCount} Samples — {calibrationState} — Ersatzwerte CH1/CH2: {_lastFrame.ReplacedSamplesChannel1}/{_lastFrame.ReplacedSamplesChannel2}";
        DrawScope();
    }

    private static string FormatMeasurements(SignalMeasurements m, SampleDomain domain)
    {
        var unit = domain == SampleDomain.Volts ? "V" : "ADC";

        return
            $"Min  {m.Minimum,8:F3} {unit}\n" +
            $"Max  {m.Maximum,8:F3} {unit}\n" +
            $"Vpp  {m.PeakToPeak,8:F3} {unit}\n" +
            $"Mean {m.Mean,8:F3} {unit}\n" +
            $"RMS  {m.Rms,8:F3} {unit}\n" +
            $"Freq {FormatFrequency(m.FrequencyHz),8}";
    }

    private static string FormatFrequency(double hz) =>
        hz >= 1_000_000 ? $"{hz / 1_000_000:F3} MHz" :
        hz >= 1_000 ? $"{hz / 1_000:F3} kHz" :
        $"{hz:F2} Hz";

    private void ScopeCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawScope();

    private void DrawScope()
    {
        ScopeCanvas.Children.Clear();

        var width = ScopeCanvas.ActualWidth;
        var height = ScopeCanvas.ActualHeight;

        if (width < 10 || height < 10)
            return;

        var gridBrush = new SolidColorBrush(Color.FromRgb(34, 39, 47));

        for (var i = 1; i < 10; i++)
        {
            var x = width * i / 10.0;
            ScopeCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = height, Stroke = gridBrush, StrokeThickness = 0.8 });
        }

        for (var i = 1; i < 8; i++)
        {
            var y = height * i / 8.0;
            ScopeCanvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 0.8 });
        }

        if (_lastFrame is null)
            return;

        DrawTrace(_lastFrame.Channel1, width, height, Color.FromRgb(255, 216, 77));
        DrawTrace(_lastFrame.Channel2, width, height, Color.FromRgb(85, 215, 255));
    }

    private void DrawTrace(IReadOnlyList<double> samples, double width, double height, Color color)
    {
        if (samples.Count < 2)
            return;

        var maxAbs = Math.Max(1.0, samples.Max(Math.Abs));
        var scaleY = height * 0.42 / maxAbs;
        var points = WaveformEnvelope.Create(samples, Math.Max(1, (int)width));
        var trace = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = 1.4 };
        foreach (var point in points)
            trace.Points.Add(new Point(
                point.SampleIndex * width / (samples.Count - 1.0),
                height / 2.0 - point.Value * scaleY));

        ScopeCanvas.Children.Add(trace);
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        UpdateButtons();
        try
        {
            await StopAcquisitionAsync();
            if (_device is not null) await _device.DisposeAsync();
        }
        catch (Exception ex) { StatusText.Text = $"Beenden: {ex.Message}"; }
        finally
        {
            _closeReady = true;
            Close();
        }
    }
}
