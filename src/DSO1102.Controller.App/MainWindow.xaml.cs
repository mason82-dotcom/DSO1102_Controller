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
    private DsoSettings _settings = new() { SampleRate = 100_000, RecordLength = 4096, TimePerDivisionSeconds = 0.001 };
    private IDsoDevice? _device;
    private CancellationTokenSource? _runCancellation;
    private AcquisitionFrame? _lastFrame;

    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
    }

    private async void ConnectSimulator_Click(object sender, RoutedEventArgs e)
    {
        await StopAcquisitionAsync();

        if (_device is not null)
            await _device.DisposeAsync();

        _settings = new DsoSettings
        {
            SampleRate = 100_000,
            RecordLength = 4096,
            TimePerDivisionSeconds = 0.001
        };

        _device = new SimulatedDsoDevice();
        await _device.ConnectAsync();
        await _device.ApplySettingsAsync(_settings);

        DeviceHeaderText.Text = $"{_device.DeviceInfo.Name} — VID {_device.DeviceInfo.VendorId:X4} / PID {_device.DeviceInfo.ProductId:X4}";
        StatusText.Text = "Simulator verbunden.";
        RunButton.IsEnabled = true;
        SingleButton.IsEnabled = true;

        await AcquireAndRenderAsync(CancellationToken.None);
    }

    private async void ConnectHardware_Click(object sender, RoutedEventArgs e)
    {
        await StopAcquisitionAsync();

        if (_device is not null)
            await _device.DisposeAsync();

        try
        {
            _device = new VoltcraftDso1102Device();
            await _device.ConnectAsync();

            var hardwareSettings = _settings with
            {
                TimePerDivisionSeconds = 0.001,
                Channel1Enabled = true,
                Channel2Enabled = true,
                Channel1VoltsPerDivision = 1.0,
                Channel2VoltsPerDivision = 1.0,
                TriggerSource = TriggerSource.Channel1,
                TriggerSlope = TriggerSlope.Rising
            };

            await _device.ApplySettingsAsync(hardwareSettings);

            DeviceHeaderText.Text =
                $"{_device.DeviceInfo.Name} — VID {_device.DeviceInfo.VendorId:X4} / PID {_device.DeviceInfo.ProductId:X4}" +
                (string.IsNullOrWhiteSpace(_device.DeviceInfo.Firmware)
                    ? string.Empty
                    : $" — {_device.DeviceInfo.Firmware}");

            StatusText.Text =
                "DSO-1102 verbunden. 1 ms/div, ADC-Counts; persistenter x86-Bridge-Server aktiv. Analog-Frontend bleibt im aktuellen Gerätezustand.";

            RunButton.IsEnabled = true;
            SingleButton.IsEnabled = true;

            await AcquireAndRenderAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (_device is not null)
            {
                await _device.DisposeAsync();
                _device = null;
            }

            RunButton.IsEnabled = false;
            SingleButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            StatusText.Text = $"DSO-1102 Verbindung fehlgeschlagen: {ex.Message}";
        }
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_device is null || !_device.IsConnected || _runCancellation is not null)
            return;

        _runCancellation = new CancellationTokenSource();
        RunButton.IsEnabled = false;
        SingleButton.IsEnabled = false;
        StopButton.IsEnabled = true;

        try
        {
            while (!_runCancellation.Token.IsCancellationRequested)
            {
                await AcquireAndRenderAsync(_runCancellation.Token);
                await Task.Delay(40, _runCancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            RunButton.IsEnabled = _device?.IsConnected == true;
            SingleButton.IsEnabled = _device?.IsConnected == true;
            StopButton.IsEnabled = false;
        }
    }

    private async void Single_Click(object sender, RoutedEventArgs e)
    {
        if (_device?.IsConnected == true)
            await AcquireAndRenderAsync(CancellationToken.None);
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        await StopAcquisitionAsync();
        StatusText.Text = "Erfassung gestoppt.";
    }

    private async Task StopAcquisitionAsync()
    {
        if (_runCancellation is null)
            return;

        _runCancellation.Cancel();
        while (_runCancellation is not null)
            await Task.Delay(10);
    }

    private async Task AcquireAndRenderAsync(CancellationToken cancellationToken)
    {
        if (_device is null)
            return;

        _lastFrame = await _device.AcquireAsync(cancellationToken);

        var ch1 = SignalAnalyzer.Measure(_lastFrame.Channel1, _lastFrame.SampleRate);
        var ch2 = SignalAnalyzer.Measure(_lastFrame.Channel2, _lastFrame.SampleRate);
        var peak = FftAnalyzer.Analyze(_lastFrame.Channel1, _lastFrame.SampleRate)
            .Skip(1)
            .OrderByDescending(x => x.Amplitude)
            .FirstOrDefault();

        Ch1MeasurementsText.Text = FormatMeasurements(ch1, _lastFrame.SampleDomain);
        Ch2MeasurementsText.Text = FormatMeasurements(ch2, _lastFrame.SampleDomain);
        var amplitudeUnit = _lastFrame.SampleDomain == SampleDomain.Volts ? "V" : "ADC";
        FftText.Text = peak is null ? "—" : $"Peak {FormatFrequency(peak.FrequencyHz)}\nAmp  {peak.Amplitude:F3} {amplitudeUnit}";
        var calibrationState = _lastFrame.IsAmplitudeCalibrated ? "kalibriert" : "unkalibrierte ADC-Domain";
        StatusText.Text = $"Capture {_lastFrame.Timestamp:HH:mm:ss.fff} — {_lastFrame.SampleCount} Samples — {calibrationState}";
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
        var drawCount = Math.Min(samples.Count, Math.Max(2, (int)width));
        var sourceStep = (samples.Count - 1.0) / (drawCount - 1.0);

        var trace = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = 1.4 };

        for (var i = 0; i < drawCount; i++)
        {
            var index = Math.Min(samples.Count - 1, (int)Math.Round(i * sourceStep));
            trace.Points.Add(new Point(
                i * width / (drawCount - 1.0),
                height / 2.0 - samples[index] * scaleY));
        }

        ScopeCanvas.Children.Add(trace);
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        await StopAcquisitionAsync();
        if (_device is not null)
            await _device.DisposeAsync();
    }
}
