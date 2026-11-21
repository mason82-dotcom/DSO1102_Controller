using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DSO1102.Controller.Core.Abstractions;
using DSO1102.Controller.Core.Models;
using DSO1102.Controller.Core.Scope;
using DSO1102.Controller.Core.SignalAnalysis;
using DSO1102.Controller.Hardware;
using Microsoft.Win32;

namespace DSO1102.Controller.App;

public partial class MainWindow : Window
{
    // The instrument always runs the runtime-verified 1 ms/div profile; the record is
    // ~524k samples at ~5 MS/s. The time base in the UI selects a window of that record.
    private static readonly DsoSettings BaseAcquisitionSettings = new()
    {
        SampleRate = 5_000_000,
        RecordLength = 524_288,
        TimePerDivisionSeconds = 0.001
    };

    private static readonly double[] AdcScales = [4, 8, 16, 32, 64];
    private static readonly double[] VoltScales = [0.05, 0.1, 0.2, 0.5, 1, 2, 5];

    // Data colours come from Themes/Theme.xaml; the fallbacks keep the drawing code usable
    // when the window is hosted without the application resources (designer, tests).
    private static readonly Color Ch1Color = ThemeColor("Ch1Color", Color.FromRgb(0xFF, 0xD8, 0x4D));
    private static readonly Color Ch2Color = ThemeColor("Ch2Color", Color.FromRgb(0x4F, 0xD1, 0xFF));
    private static readonly Color TriggerColor = Color.FromRgb(0xFF, 0x9F, 0x43);
    private static readonly Color CursorColor = Color.FromRgb(0xB7, 0x94, 0xF6);
    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x26)));
    private static readonly Brush AxisBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2C, 0x32, 0x3D)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(ThemeColor("TextSecondaryColor", Color.FromRgb(0xA3, 0xAB, 0xB8))));
    private static readonly FontFamily MonoFamily = new("Cascadia Mono, Consolas");
    private static readonly Brush LabelBackdrop = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0x07, 0x09, 0x0C)));

    private static readonly string[] ReadoutNames =
        ["Frequenz", "Periode", "Tastgrad", "Spitze-Spitze", "Minimum", "Maximum", "Mittelwert", "RMS", "AC-RMS"];

    private TextBlock[] _ch1Readouts = [];
    private TextBlock[] _ch2Readouts = [];
    private DsoSettings _acquisition = BaseAcquisitionSettings;
    private bool _connectionLost;

    private readonly string _settingsPath = ScopeSettingsStore.DefaultPath;
    private ScopeSettingsProfile _profile;
    private SampleDomain _domain = SampleDomain.AdcCounts;
    private ScopeViewSettings _settings;

    private IDsoDevice? _device;
    private CancellationTokenSource? _runCancellation;
    private TaskCompletionSource? _operationCompletion;
    private bool _closing;
    private bool _closeReady;
    private bool _suppressEvents;
    private int _reprocessVersion;
    private int _framesWithoutTrigger;

    private ScopeView? _view;
    private double? _cursorA;
    private double? _cursorB;

    public MainWindow()
    {
        InitializeComponent();

        _profile = ScopeSettingsStore.Load(_settingsPath);
        _settings = _profile.For(_domain);

        _ch1Readouts = BuildReadouts(Ch1Readouts);
        _ch2Readouts = BuildReadouts(Ch2Readouts);

        foreach (var window in Enum.GetValues<WindowFunction>())
            FftWindowCombo.Items.Add(new ComboBoxItem { Content = WindowName(window), Tag = window });

        ConfigureDomainControls(_domain);
        ApplySettingsToControls();
        UpdateButtons();
        Closing += MainWindow_Closing;
    }

    // ---------------------------------------------------------------- connection

    private async void ConnectSimulator_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(token => ConnectAsync(new SimulatedDsoDevice(), SampleDomain.Volts, token));

    private async void ConnectHardware_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(token => ConnectAsync(new VoltcraftDso1102Device(), SampleDomain.AdcCounts, token));

    private async Task ConnectAsync(IDsoDevice device, SampleDomain expectedDomain, CancellationToken token)
    {
        await DisposeDeviceAsync();
        _device = device;
        ClearView();
        SwitchDomain(expectedDomain);
        DeviceHeaderText.Text = "Verbindung wird aufgebaut …";
        StatusText.Text = "Verbindung wird aufgebaut …";
        await device.ConnectAsync(token);
        _connectionLost = false;
        _acquisition = ReadAcquisitionSettings();
        await device.ApplySettingsAsync(_acquisition, token);
        UpdateDeviceHeader();
        UpdateButtons();
        await AcquireOnceAsync(waitForTrigger: false, token);
    }

    private void UpdateDeviceHeader()
    {
        var device = _device;
        DeviceHeaderText.Text = device is not { IsConnected: true }
            ? "Nicht verbunden"
            : string.IsNullOrWhiteSpace(device.DeviceInfo.Firmware)
                ? device.DeviceInfo.Name
                : $"{device.DeviceInfo.Name} — {device.DeviceInfo.Firmware}";
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await StopAcquisitionAsync();
        try
        {
            await DisposeDeviceAsync();
            StatusText.Text = "Verbindung getrennt. Die letzte Aufnahme bleibt sichtbar.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Trennen nicht vollständig: {ex.Message}";
        }
        finally
        {
            DeviceHeaderText.Text = "Nicht verbunden";
            UpdateButtons();
        }
    }

    private async Task DisposeDeviceAsync()
    {
        var device = _device;
        _device = null;
        if (device is not null)
            await device.DisposeAsync();
    }

    // ---------------------------------------------------------------- acquisition

    private async void Run_Click(object sender, RoutedEventArgs e) => await StartRunAsync();

    private async void Single_Click(object sender, RoutedEventArgs e) => await StartSingleAsync();

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopAcquisitionAsync();

    private async Task StartRunAsync()
    {
        if (!CanAcquire) return;
        await RunOperationAsync(async token =>
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await AcquireOnceAsync(waitForTrigger: false, token);
                await Task.Delay(20, token);
            }
        });
    }

    private async Task StartSingleAsync()
    {
        if (!CanAcquire) return;
        await RunOperationAsync(token => AcquireOnceAsync(waitForTrigger: true, token));
    }

    private bool CanAcquire => _device?.IsConnected == true && _runCancellation is null && !_closing;

    /// <summary>
    /// Captures frames until one may be displayed. In Normal mode a frame without a
    /// trigger edge never replaces the display; with <paramref name="waitForTrigger"/>
    /// the method keeps capturing until an edge is found (Single).
    /// </summary>
    private async Task AcquireOnceAsync(bool waitForTrigger, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var device = _device ?? throw new InvalidOperationException("Kein Gerät verbunden.");
            var frame = await device.AcquireAsync(token);

            if (frame.SampleDomain != _domain)
                SwitchDomain(frame.SampleDomain);

            var settings = _settings;
            var view = await Task.Run(() => ScopeProcessor.Process(frame, settings), token);
            // Controls may have changed while the frame was processed; never show a view
            // that disagrees with them (a pending reprocess would be superseded below).
            while (!Equals(settings, _settings))
            {
                settings = _settings;
                var previous = view;
                view = await Task.Run(() => ScopeProcessor.Process(frame, settings, previous), token);
            }
            token.ThrowIfCancellationRequested();

            if (view.TriggerState != TriggerState.Waiting)
            {
                _framesWithoutTrigger = 0;
                ShowView(view);
                UpdateStateIndicators();
                return;
            }

            _framesWithoutTrigger++;
            UpdateStateIndicators();
            StatusText.Text = $"Warte auf Trigger ({SourceName(settings.TriggerSource)} {SlopeName(settings.TriggerSlope)} bei {FormatValue(settings.TriggerLevel)}) — {_framesWithoutTrigger} Aufnahme(n) ohne Flanke.";
            if (!waitForTrigger)
                return;
        }
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (_runCancellation is not null || _closing) return;
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runCancellation = cancellation;
        _operationCompletion = completion;
        UpdateButtons();
        try
        {
            await action(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusText.Text = _view is null ? "Vorgang abgebrochen." : "Erfassung angehalten. Die letzte Aufnahme bleibt sichtbar.";
            if (_device is { IsConnected: false })
            {
                // Connect was cancelled before it finished: release the half-open bridge.
                try { await DisposeDeviceAsync(); }
                catch (Exception cleanup) { StatusText.Text += $" (Bereinigung: {cleanup.Message})"; }
            }
            UpdateDeviceHeader();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Verbindung unterbrochen: {ex.Message} — Gerät prüfen und erneut verbinden.";
            _connectionLost = true;
            DeviceHeaderText.Text = "Nicht verbunden";
            try { await DisposeDeviceAsync(); }
            catch (Exception cleanup) { StatusText.Text += $" (Bereinigung: {cleanup.Message})"; }
        }
        finally
        {
            _runCancellation = null;
            _operationCompletion = null;
            UpdateButtons();
            completion.TrySetResult();
        }
    }

    private async Task StopAcquisitionAsync()
    {
        var completion = _operationCompletion;
        _runCancellation?.Cancel();
        if (completion is not null) await completion.Task;
    }

    private void UpdateButtons()
    {
        var idle = _runCancellation is null && !_closing;
        var connected = _device?.IsConnected == true;
        ConnectSimulatorButton.IsEnabled = idle;
        ConnectHardwareButton.IsEnabled = idle;
        RunButton.IsEnabled = SingleButton.IsEnabled = idle && connected;
        StopButton.IsEnabled = !idle && !_closing;
        DisconnectButton.IsEnabled = !_closing && _device is not null;
        var hasView = _view is not null;
        ExportWindowCsvButton.IsEnabled = ExportRecordCsvButton.IsEnabled = ExportPngButton.IsEnabled = hasView && !_closing;
        ClearCursorsButton.IsEnabled = _cursorA is not null || _cursorB is not null;
        UpdateStateIndicators();
    }

    private void UpdateStateIndicators()
    {
        var running = _runCancellation is not null;
        var connected = _device?.IsConnected == true;
        var (dotKey, text, textKey) =
            _connectionLost ? ("DangerBrush", "Verbindung unterbrochen", "DangerBrush")
            : running && _framesWithoutTrigger > 0 ? ("WarningBrush", "Wartet auf Trigger", "WarningBrush")
            : running && connected ? ("AccentBrush", "Erfassung läuft", "AccentHoverBrush")
            : running ? ("AccentBrush", "Verbindet …", "TextSecondaryBrush")
            : connected ? ("SuccessBrush", "Angehalten", "TextSecondaryBrush")
            : ("TextMutedBrush", "Nicht verbunden", "TextMutedBrush");

        StatusDot.Fill = (Brush)FindResource(dotKey);
        RunStateText.Text = text;
        RunStateText.Foreground = (Brush)FindResource(textKey);
    }

    // ---------------------------------------------------------------- settings

    private void SwitchDomain(SampleDomain domain)
    {
        if (domain == _domain) return;
        _profile = _profile.With(_domain, _settings);
        _domain = domain;
        _settings = _profile.For(domain);
        ConfigureDomainControls(domain);
        ApplySettingsToControls();
    }

    private void ConfigureDomainControls(SampleDomain domain)
    {
        _suppressEvents = true;
        try
        {
            var scales = domain == SampleDomain.Volts ? VoltScales : AdcScales;
            foreach (var combo in new[] { Ch1ScaleCombo, Ch2ScaleCombo })
            {
                combo.Items.Clear();
                foreach (var scale in scales)
                    combo.Items.Add(new ComboBoxItem { Content = $"{FormatValue(scale, domain)}/div", Tag = scale });
            }

            var (min, max, small) = domain == SampleDomain.Volts ? (-20.0, 20.0, 0.05) : (0.0, 255.0, 1.0);
            foreach (var slider in new[] { Ch1CenterSlider, Ch2CenterSlider, TriggerLevelSlider })
            {
                slider.Minimum = min;
                slider.Maximum = max;
                slider.SmallChange = small;
                slider.LargeChange = small * 10;
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void ApplySettingsToControls()
    {
        _suppressEvents = true;
        try
        {
            var s = _settings;
            RefreshTimeBaseItems();
            TriggerPositionSlider.Value = s.TriggerPositionPercent;
            (s.TriggerMode == SoftwareTriggerMode.Normal ? TriggerNormalRadio : TriggerAutoRadio).IsChecked = true;
            (s.TriggerSource == TriggerSource.Channel2 ? TriggerCh2Radio : TriggerCh1Radio).IsChecked = true;
            (s.TriggerSlope == TriggerSlope.Falling ? SlopeFallingRadio : SlopeRisingRadio).IsChecked = true;
            TriggerLevelSlider.Value = s.TriggerLevel;
            Ch1VisibleCheck.IsChecked = s.Channel1.Visible;
            Ch2VisibleCheck.IsChecked = s.Channel2.Visible;
            SelectScale(Ch1ScaleCombo, s.Channel1.UnitsPerDivision);
            SelectScale(Ch2ScaleCombo, s.Channel2.UnitsPerDivision);
            Ch1CenterSlider.Value = s.Channel1.CenterValue;
            Ch2CenterSlider.Value = s.Channel2.CenterValue;
            SpectrumVisibleCheck.IsChecked = s.ShowSpectrum;
            FftWindowCombo.SelectedItem = FftWindowCombo.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => (WindowFunction)i.Tag == s.FftWindow);
        }
        finally
        {
            _suppressEvents = false;
        }

        UpdateSettingLabels();
    }

    private void RefreshTimeBaseItems()
    {
        var frame = _view?.Frame;
        var steps = frame is null
            ? TimeBaseSteps.All
            : TimeBaseSteps.FittingRecord(frame.SampleCount, frame.SampleRate);

        var wanted = _settings.TimePerDivisionSeconds;
        var previous = _suppressEvents;
        _suppressEvents = true;
        try
        {
            TimeBaseCombo.Items.Clear();
            foreach (var step in steps)
                TimeBaseCombo.Items.Add(new ComboBoxItem { Content = TimeBaseSteps.Format(step), Tag = step });

            var best = steps.MinBy(step => Math.Abs(Math.Log(step / wanted)));
            TimeBaseCombo.SelectedItem = TimeBaseCombo.Items.Cast<ComboBoxItem>().First(i => (double)i.Tag == best);
            _settings = _settings with { TimePerDivisionSeconds = best };
        }
        finally
        {
            _suppressEvents = previous;
        }
    }

    private static void SelectScale(ComboBox combo, double value)
    {
        var items = combo.Items.Cast<ComboBoxItem>().ToArray();
        if (items.Length == 0) return;
        combo.SelectedItem = items.MinBy(i => Math.Abs(Math.Log((double)i.Tag / value)));
    }

    private ScopeViewSettings ReadSettingsFromControls()
    {
        static double TagOr(ComboBox combo, double fallback) =>
            combo.SelectedItem is ComboBoxItem { Tag: double value } ? value : fallback;

        return new ScopeViewSettings
        {
            TimePerDivisionSeconds = TagOr(TimeBaseCombo, _settings.TimePerDivisionSeconds),
            TriggerPositionPercent = TriggerPositionSlider.Value,
            TriggerMode = TriggerNormalRadio.IsChecked == true ? SoftwareTriggerMode.Normal : SoftwareTriggerMode.Auto,
            TriggerSource = TriggerCh2Radio.IsChecked == true ? TriggerSource.Channel2 : TriggerSource.Channel1,
            TriggerSlope = SlopeFallingRadio.IsChecked == true ? TriggerSlope.Falling : TriggerSlope.Rising,
            TriggerLevel = TriggerLevelSlider.Value,
            TriggerHysteresisFraction = _settings.TriggerHysteresisFraction,
            Channel1 = new ChannelView
            {
                Visible = Ch1VisibleCheck.IsChecked == true,
                UnitsPerDivision = TagOr(Ch1ScaleCombo, _settings.Channel1.UnitsPerDivision),
                CenterValue = Ch1CenterSlider.Value
            },
            Channel2 = new ChannelView
            {
                Visible = Ch2VisibleCheck.IsChecked == true,
                UnitsPerDivision = TagOr(Ch2ScaleCombo, _settings.Channel2.UnitsPerDivision),
                CenterValue = Ch2CenterSlider.Value
            },
            ShowSpectrum = SpectrumVisibleCheck.IsChecked == true,
            FftWindow = FftWindowCombo.SelectedItem is ComboBoxItem { Tag: WindowFunction window } ? window : _settings.FftWindow
        }.Normalize();
    }

    private void ViewSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || !IsInitialized) return;
        var previous = _settings;
        _settings = ReadSettingsFromControls();
        _profile = _profile.With(_domain, _settings);
        UpdateSettingLabels();

        // Cursor times are relative to the trigger point and stay meaningful across
        // most changes; a different time base would put them off-screen.
        if (Math.Abs(previous.TimePerDivisionSeconds - _settings.TimePerDivisionSeconds) > 1e-15)
            ClearCursors();

        _ = ReprocessAsync();
    }

    private void UpdateSettingLabels()
    {
        TriggerPositionLabel.Content = $"_Triggerposition: {_settings.TriggerPositionPercent:0} %";
        TriggerLevelLabel.Content = $"_Pegel: {FormatValue(_settings.TriggerLevel)}";
        Ch1CenterLabel.Content = $"CH1 _Mittellinie: {FormatValue(_settings.Channel1.CenterValue)}";
        Ch2CenterLabel.Content = $"CH2 M_ittellinie: {FormatValue(_settings.Channel2.CenterValue)}";
        SpectrumRow.Height = _settings.ShowSpectrum ? new GridLength(200) : new GridLength(0);
    }

    private DsoSettings ReadAcquisitionSettings()
    {
        static double Range(RadioButton half, RadioButton two) => half.IsChecked == true ? 0.5 : two.IsChecked == true ? 2.0 : 1.0;
        static ChannelCoupling Coupling(RadioButton ac, RadioButton gnd) =>
            gnd.IsChecked == true ? ChannelCoupling.Ground : ac.IsChecked == true ? ChannelCoupling.AC : ChannelCoupling.DC;

        return BaseAcquisitionSettings with
        {
            ExperimentalAnalogControl = AnalogControlCheck.IsChecked == true,
            Channel1VoltsPerDivision = Range(Ch1Range05, Ch1Range2),
            Channel2VoltsPerDivision = Range(Ch2Range05, Ch2Range2),
            Channel1Coupling = Coupling(Ch1CouplingAc, Ch1CouplingGnd),
            Channel2Coupling = Coupling(Ch2CouplingAc, Ch2CouplingGnd),
            Channel1ProbeFactor = Ch1Probe10.IsChecked == true ? 10 : 1,
            Channel2ProbeFactor = Ch2Probe10.IsChecked == true ? 10 : 1
        };
    }

    /// <summary>
    /// Front-end changes take effect with the next capture: the device runs the guarded
    /// analog sequence once and then returns to the plain frame command.
    /// </summary>
    private async void AnalogSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || !IsInitialized) return;
        var settings = ReadAcquisitionSettings();
        _acquisition = settings;

        var device = _device;
        if (device?.IsConnected != true)
            return;

        try
        {
            await device.ApplySettingsAsync(settings);
            StatusText.Text = settings.ExperimentalAnalogControl
                ? "Analog-Frontend wird mit der nächsten Aufnahme gesetzt (experimentell, nominelle Volt)."
                : "Analog-Frontend nicht mehr gesteuert: das Gerät behält die zuletzt gesetzten Werte, Anzeige in ADC-Werten.";
            if (_runCancellation is null)
                await StartSingleAsync();
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException)
        {
            StatusText.Text = $"Analog-Einstellung nicht übernommen: {ex.Message}";
        }
    }

    private void TriggerLevelToMid_Click(object sender, RoutedEventArgs e)
    {
        if (_view is null)
        {
            StatusText.Text = "Für die Signalmitte zuerst eine Aufnahme machen.";
            return;
        }

        var m = SignalAnalyzer.Measure(_view.Samples(_settings.TriggerSource), _view.WindowStart, _view.WindowLength, _view.SampleRate);
        TriggerLevelSlider.Value = Math.Round((m.Minimum + m.Maximum) / 2, _domain == SampleDomain.Volts ? 3 : 0);
    }

    private bool _reprocessRunning;
    private bool _reprocessPending;

    /// <summary>
    /// Re-evaluates the displayed frame with the current settings. Slider drags fire many
    /// changes; only one evaluation runs at a time and the latest settings win.
    /// </summary>
    private async Task ReprocessAsync()
    {
        if (_reprocessRunning)
        {
            _reprocessPending = true;
            return;
        }

        _reprocessRunning = true;
        try
        {
            do
            {
                _reprocessPending = false;
                var frame = _view?.Frame;
                if (frame is null)
                {
                    DrawAll();
                    return;
                }

                var version = ++_reprocessVersion;
                var settings = _settings;
                var previous = _view;
                var view = await Task.Run(() => ScopeProcessor.Process(frame, settings, previous));
                if (version != _reprocessVersion || _view is null) continue;

                // Re-evaluating a stored frame always shows it, even in Normal mode without
                // an edge, so the effect of changed settings stays visible.
                ShowView(view, fromReprocess: true);
            }
            while (_reprocessPending);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Anzeige konnte nicht aktualisiert werden: {ex.Message}";
        }
        finally
        {
            _reprocessRunning = false;
        }
    }

    // ---------------------------------------------------------------- display

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

        var frame = view.Frame;
        ShowReadouts(_ch1Readouts, view.Channel1);
        ShowReadouts(_ch2Readouts, view.Channel2);
        FftText.Text = view.SpectrumPeak is { } peak
            ? $"Spitze  {TimeBaseSteps.FormatFrequency(peak.FrequencyHz)}\nAmpl.   {FormatValue(peak.Amplitude)}\nAuflös. {TimeBaseSteps.FormatFrequency(SpectrumResolution(view))}"
            : "—";

        var triggerText = view.TriggerState switch
        {
            TriggerState.Triggered => "getriggert",
            TriggerState.AutoFreeRun => "Auto ohne Flanke (Recordmitte)",
            _ => "keine Flanke"
        };
        var calibration = frame.IsNominalScale ? "nominelle Volt (unkalibriert)"
            : frame.IsAmplitudeCalibrated ? "Volt" : "unkalibrierte ADC-Werte";
        var replaced = frame.ReplacedSamplesChannel1 + frame.ReplacedSamplesChannel2 > 0
            ? $" — Ersatzwerte CH1/CH2: {frame.ReplacedSamplesChannel1}/{frame.ReplacedSamplesChannel2}"
            : string.Empty;
        StatusText.Text =
            $"Aufnahme {frame.Timestamp:HH:mm:ss.fff} — {frame.SampleCount:N0} Samples @ {FormatRate(frame.SampleRate)} — " +
            $"Fenster {TimeBaseSteps.FormatTime(view.WindowDurationSeconds)} — Trigger {triggerText} — {calibration}{replaced}";

        UpdateButtons();
        UpdateCursorText();
        DrawAll();
    }

    private void ClearView()
    {
        _view = null;
        _reprocessVersion++;
        ClearCursors();
        ShowReadouts(_ch1Readouts, null);
        ShowReadouts(_ch2Readouts, null);
        FftText.Text = "—";
        DrawAll();
        UpdateButtons();
    }

    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawAll();

    private void DrawAll()
    {
        DrawScope();
        DrawSpectrum();
    }

    private void DrawScope()
    {
        var canvas = ScopeCanvas;
        canvas.Children.Clear();
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width < 20 || height < 20) return;

        DrawGrid(canvas, width, height, ScopeViewSettings.HorizontalDivisions, ScopeViewSettings.VerticalDivisions);

        var view = _view;
        if (view is null)
        {
            AddLabel(canvas, "Keine Aufnahme. Simulator oder DSO-1102 verbinden.", width / 2 - 170, height / 2 - 10, LabelBrush);
            return;
        }

        var s = view.Settings;
        if (s.Channel1.Visible) DrawTrace(canvas, view, view.Frame.Channel1, s.Channel1, Ch1Color, width, height);
        if (s.Channel2.Visible) DrawTrace(canvas, view, view.Frame.Channel2, s.Channel2, Ch2Color, width, height);

        // Trigger position (vertical) and level (on the source channel's scale).
        var triggerBrush = Freeze(new SolidColorBrush(TriggerColor));
        var triggerX = width * s.TriggerPositionPercent / 100.0;
        canvas.Children.Add(new Line
        {
            X1 = triggerX, X2 = triggerX, Y1 = 0, Y2 = height,
            Stroke = triggerBrush, StrokeThickness = 1, StrokeDashArray = [4, 4],
            Opacity = view.TriggerState == TriggerState.Triggered ? 0.8 : 0.35
        });
        var sourceView = s.TriggerSource == TriggerSource.Channel2 ? s.Channel2 : s.Channel1;
        var levelY = ValueToY(s.TriggerLevel, sourceView, height);
        if (levelY >= 0 && levelY <= height)
        {
            canvas.Children.Add(new Line { X1 = 0, X2 = 14, Y1 = levelY, Y2 = levelY, Stroke = triggerBrush, StrokeThickness = 2 });
            AddLabel(canvas, "T", 16, levelY - 9, triggerBrush);
        }

        DrawCursor(canvas, view, _cursorA, "A", width, height);
        DrawCursor(canvas, view, _cursorB, "B", width, height);

        var unit = UnitName(view.Frame.SampleDomain);
        var header = new StringBuilder();
        if (s.Channel1.Visible) header.Append($"CH1 {FormatValue(s.Channel1.UnitsPerDivision)}/div   ");
        if (s.Channel2.Visible) header.Append($"CH2 {FormatValue(s.Channel2.UnitsPerDivision)}/div   ");
        AddLabel(canvas, header.ToString().TrimEnd(), 8, 6, LabelBrush);
        AddLabel(canvas, $"{TimeBaseSteps.Format(view.WindowDurationSeconds / ScopeViewSettings.HorizontalDivisions)}   Fenster {TimeBaseSteps.FormatTime(view.WindowStartTimeSeconds)} … {TimeBaseSteps.FormatTime(view.WindowStartTimeSeconds + view.WindowDurationSeconds)}",
            8, height - 22, LabelBrush);

        var state = view.TriggerState switch
        {
            TriggerState.Triggered => $"Trig {SourceName(s.TriggerSource)} {SlopeName(s.TriggerSlope)}",
            TriggerState.AutoFreeRun => "Auto (keine Flanke)",
            _ => "Keine Flanke"
        };
        AddLabel(canvas, state, width - 160, 6, triggerBrush);
        if (!view.Frame.IsAmplitudeCalibrated)
            AddLabel(canvas, view.Frame.IsNominalScale ? "V nominell (unkalibriert)" : $"Einheit: {unit} (unkalibriert)", width - 210, height - 22, LabelBrush);
    }

    private static void DrawTrace(Canvas canvas, ScopeView view, IReadOnlyList<double> samples, ChannelView channel, Color color, double width, double height)
    {
        if (view.WindowLength < 2 || samples.Count < view.WindowStart + view.WindowLength) return;

        var points = WaveformEnvelope.Create(samples, view.WindowStart, view.WindowLength, Math.Max(1, (int)width));
        var trace = new Polyline { Stroke = Freeze(new SolidColorBrush(color)), StrokeThickness = 1.3 };
        var scaleX = width / (view.WindowLength - 1.0);
        foreach (var point in points)
        {
            var y = Math.Clamp(ValueToY(point.Value, channel, height), -2, height + 2);
            trace.Points.Add(new Point((point.SampleIndex - view.WindowStart) * scaleX, y));
        }
        canvas.Children.Add(trace);

        // Ground/centre reference marker on the right edge.
        var centerY = height / 2;
        canvas.Children.Add(new Polygon
        {
            Points = [new Point(width, centerY - 5), new Point(width - 8, centerY), new Point(width, centerY + 5)],
            Fill = Freeze(new SolidColorBrush(color)),
            Opacity = 0.7
        });
    }

    private static double ValueToY(double value, ChannelView channel, double height) =>
        height / 2 - (value - channel.CenterValue) / channel.UnitsPerDivision * (height / ScopeViewSettings.VerticalDivisions);

    private static void DrawGrid(Canvas canvas, double width, double height, int columns, int rows)
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

    private static void AddLabel(Canvas canvas, string text, double x, double y, Brush brush)
    {
        // Semi-opaque scope background keeps labels readable where traces pass underneath.
        var label = new TextBlock
        {
            Text = text, Foreground = brush, FontFamily = MonoFamily, FontSize = 12,
            Background = LabelBackdrop, Padding = new Thickness(4, 1, 4, 1)
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        canvas.Children.Add(label);
    }

    private void DrawSpectrum()
    {
        var canvas = SpectrumCanvas;
        canvas.Children.Clear();
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width < 20 || height < 20 || !_settings.ShowSpectrum) return;

        const int dbDivisions = 8;
        const double dbPerDivision = 10;
        DrawGrid(canvas, width, height, 10, dbDivisions);

        var view = _view;
        if (view is null || view.Spectrum.Count < 2)
        {
            AddLabel(canvas, "Spektrum erscheint nach der ersten Aufnahme.", 8, 6, LabelBrush);
            return;
        }

        var bins = view.Spectrum;
        var maxFrequency = bins[^1].FrequencyHz;
        static double Db(double amplitude) => 20 * Math.Log10(Math.Max(amplitude, 1e-12));
        var peakDb = Db(bins.Skip(1).Max(b => b.Amplitude));
        var top = Math.Ceiling(peakDb / dbPerDivision) * dbPerDivision;
        var bottom = top - dbPerDivision * dbDivisions;

        var buckets = Math.Max(1, (int)width);
        var trace = new Polyline
        {
            Stroke = Freeze(new SolidColorBrush(view.Settings.TriggerSource == TriggerSource.Channel2 ? Ch2Color : Ch1Color)),
            StrokeThickness = 1.1
        };
        var index = 1; // skip DC (mean removed)
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
        AddLabel(canvas, $"{top:0} {unit} · 10 dB/div", 8, 6, LabelBrush);
        AddLabel(canvas, "0 Hz", 8, height - 20, LabelBrush);
        AddLabel(canvas, TimeBaseSteps.FormatFrequency(maxFrequency), width - 90, height - 20, LabelBrush);
        if (view.SpectrumPeak is { } peak)
        {
            var x = peak.FrequencyHz / maxFrequency * width;
            AddLabel(canvas, $"▼ {TimeBaseSteps.FormatFrequency(peak.FrequencyHz)}", Math.Clamp(x - 6, 0, width - 120), 22, LabelBrush);
        }
    }

    // ---------------------------------------------------------------- cursors

    private void ScopeCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => SetCursor(e, isA: true);

    private void ScopeCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e) => SetCursor(e, isA: false);

    private void SetCursor(MouseButtonEventArgs e, bool isA)
    {
        var view = _view;
        var width = ScopeCanvas.ActualWidth;
        if (view is null || width < 2 || view.WindowLength < 2) return;

        var x = Math.Clamp(e.GetPosition(ScopeCanvas).X, 0, width);
        var time = view.WindowStartTimeSeconds + x / width * view.WindowDurationSeconds;
        if (isA) _cursorA = time; else _cursorB = time;
        UpdateCursorText();
        UpdateButtons();
        DrawScope();
        e.Handled = true;
    }

    /// <summary>Ctrl+1/Ctrl+2 place A/B at the trigger point; Ctrl(+Shift)+arrow moves B (A) by 0.1 div.</summary>
    private void PlaceCursorFromKeyboard(bool isA, double? deltaDivisions)
    {
        var view = _view;
        if (view is null) return;
        var step = view.WindowDurationSeconds / ScopeViewSettings.HorizontalDivisions;
        var current = isA ? _cursorA : _cursorB;
        var time = deltaDivisions is { } d ? (current ?? 0) + d * step : 0;
        time = Math.Clamp(time, view.WindowStartTimeSeconds, view.WindowStartTimeSeconds + view.WindowDurationSeconds);
        if (isA) _cursorA = time; else _cursorB = time;
        UpdateCursorText();
        UpdateButtons();
        DrawScope();
    }

    private void ClearCursors_Click(object sender, RoutedEventArgs e)
    {
        ClearCursors();
        DrawScope();
    }

    private void ClearCursors()
    {
        _cursorA = _cursorB = null;
        UpdateCursorText();
        UpdateButtons();
    }

    private void DrawCursor(Canvas canvas, ScopeView view, double? time, string name, double width, double height)
    {
        if (time is not { } t || view.WindowDurationSeconds <= 0) return;
        var x = (t - view.WindowStartTimeSeconds) / view.WindowDurationSeconds * width;
        if (x < 0 || x > width) return;
        var brush = Freeze(new SolidColorBrush(CursorColor));
        canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = height, Stroke = brush, StrokeThickness = 1 });
        AddLabel(canvas, name, Math.Min(x + 3, width - 12), 24, brush);
    }

    private void UpdateCursorText()
    {
        var view = _view;
        if (view is null || (_cursorA is null && _cursorB is null))
        {
            CursorText.Text = "Linksklick setzt A, Rechtsklick setzt B.";
            return;
        }

        var text = new StringBuilder();
        double? ValueAt(IReadOnlyList<double> samples, double time)
        {
            var index = (int)Math.Round(view.WindowStart + (time - view.WindowStartTimeSeconds) * view.SampleRate);
            return index >= 0 && index < samples.Count ? samples[index] : null;
        }

        void Line(string name, double? time)
        {
            if (time is not { } t) return;
            text.AppendLine($"{name}: t {TimeBaseSteps.FormatTime(t),11}");
            text.AppendLine($"   CH1 {FormatNullable(ValueAt(view.Frame.Channel1, t))}  CH2 {FormatNullable(ValueAt(view.Frame.Channel2, t))}");
        }

        Line("A", _cursorA);
        Line("B", _cursorB);
        if (_cursorA is { } a && _cursorB is { } b)
        {
            var dt = Math.Abs(b - a);
            text.AppendLine($"Δt {TimeBaseSteps.FormatTime(dt)}   1/Δt {TimeBaseSteps.FormatFrequency(dt > 0 ? 1 / dt : 0)}");
            var d1 = ValueAt(view.Frame.Channel1, b) - ValueAt(view.Frame.Channel1, a);
            var d2 = ValueAt(view.Frame.Channel2, b) - ValueAt(view.Frame.Channel2, a);
            text.Append($"ΔCH1 {FormatNullable(d1)}  ΔCH2 {FormatNullable(d2)}");
        }

        CursorText.Text = text.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- export

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

    // ---------------------------------------------------------------- keyboard / lifecycle

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F5:
                e.Handled = true;
                await StartRunAsync();
                break;
            case Key.F6:
                e.Handled = true;
                await StartSingleAsync();
                break;
            case Key.Escape:
                e.Handled = true;
                if (_runCancellation is not null) await StopAcquisitionAsync();
                else if (_cursorA is not null || _cursorB is not null) { ClearCursors(); DrawScope(); }
                break;
            case Key.D1 or Key.NumPad1 when Keyboard.Modifiers == ModifierKeys.Control && _view is not null:
                e.Handled = true;
                PlaceCursorFromKeyboard(isA: true, deltaDivisions: null);
                break;
            case Key.D2 or Key.NumPad2 when Keyboard.Modifiers == ModifierKeys.Control && _view is not null:
                e.Handled = true;
                PlaceCursorFromKeyboard(isA: false, deltaDivisions: null);
                break;
            case Key.Left or Key.Right when (Keyboard.Modifiers & ModifierKeys.Control) != 0 && _view is not null:
                e.Handled = true;
                PlaceCursorFromKeyboard(
                    isA: (Keyboard.Modifiers & ModifierKeys.Shift) != 0,
                    deltaDivisions: e.Key == Key.Left ? -0.1 : 0.1);
                break;
            case Key.S when Keyboard.Modifiers == ModifierKeys.Control && _view is not null:
                e.Handled = true;
                await ExportCsvAsync(visibleWindowOnly: true);
                break;
        }
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
            await DisposeDeviceAsync();
        }
        catch (Exception ex) { StatusText.Text = $"Beenden: {ex.Message}"; }
        finally
        {
            try { ScopeSettingsStore.Save(_settingsPath, _profile.With(_domain, _settings)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _closeReady = true;
            Close();
        }
    }

    // ---------------------------------------------------------------- formatting

    private static TextBlock[] BuildReadouts(UniformGrid host)
    {
        host.Children.Clear();
        var values = new TextBlock[ReadoutNames.Length];
        for (var i = 0; i < ReadoutNames.Length; i++)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            cell.Children.Add(new TextBlock { Text = ReadoutNames[i], Style = (Style)host.FindResource("ReadoutLabel") });
            values[i] = new TextBlock { Text = "—", Style = (Style)host.FindResource("ReadoutValue"), TextTrimming = TextTrimming.CharacterEllipsis };
            cell.Children.Add(values[i]);
            host.Children.Add(cell);
        }
        return values;
    }

    private void ShowReadouts(TextBlock[] cells, SignalMeasurements? m)
    {
        if (cells.Length != ReadoutNames.Length) return;
        if (m is null)
        {
            foreach (var cell in cells) cell.Text = "—";
            return;
        }

        cells[0].Text = TimeBaseSteps.FormatFrequency(m.FrequencyHz);
        cells[1].Text = m.PeriodSeconds > 0 ? TimeBaseSteps.FormatTime(m.PeriodSeconds) : "—";
        cells[2].Text = double.IsFinite(m.DutyCycle) ? $"{m.DutyCycle * 100:F1} %" : "—";
        cells[3].Text = FormatValue(m.PeakToPeak);
        cells[4].Text = FormatValue(m.Minimum);
        cells[5].Text = FormatValue(m.Maximum);
        cells[6].Text = FormatValue(m.Mean);
        cells[7].Text = FormatValue(m.Rms);
        cells[8].Text = FormatValue(m.AcRms);
    }

    private static Color ThemeColor(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) is Color color ? color : fallback;

    private string FormatValue(double value) => FormatValue(value, _domain);

    private static string FormatValue(double value, SampleDomain domain) => domain == SampleDomain.Volts
        ? Math.Abs(value) < 1 && value != 0
            ? $"{(value * 1000).ToString("0.#", CultureInfo.CurrentCulture)} mV"
            : $"{value.ToString("0.###", CultureInfo.CurrentCulture)} V"
        : $"{value.ToString("0.#", CultureInfo.CurrentCulture)} ADC";

    private string FormatNullable(double? value) => value is { } v ? FormatValue(v) : "—";

    private static string UnitName(SampleDomain domain) => domain == SampleDomain.Volts ? "V" : "ADC";

    private static string FormatRate(double rate) =>
        rate >= 1e6 ? $"{rate / 1e6:0.###} MS/s" : rate >= 1e3 ? $"{rate / 1e3:0.###} kS/s" : $"{rate:0.#} S/s";

    private static double SpectrumResolution(ScopeView view) =>
        view.Spectrum.Count > 1 ? view.Spectrum[1].FrequencyHz - view.Spectrum[0].FrequencyHz : 0;

    private static string SourceName(TriggerSource source) => source == TriggerSource.Channel2 ? "CH2" : "CH1";

    private static string SlopeName(TriggerSlope slope) => slope == TriggerSlope.Falling ? "fallend" : "steigend";

    private static string WindowName(WindowFunction window) => window switch
    {
        WindowFunction.Rectangular => "Rechteck",
        WindowFunction.Hann => "Hann",
        WindowFunction.Hamming => "Hamming",
        WindowFunction.Blackman => "Blackman",
        _ => window.ToString()
    };

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
