using System.Windows;
using System.Windows.Controls;
using DSO1102.Controller.Core.Models;
using DSO1102.Controller.Core.Presentation;
using DSO1102.Controller.Core.Scope;
using DSO1102.Controller.Core.SignalAnalysis;

namespace DSO1102.Controller.App;

// Mapping between controls and settings, domain switching and re-evaluation of the shown frame.
public partial class MainWindow
{
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
                    combo.Items.Add(new ComboBoxItem { Content = $"{DisplayFormat.Value(scale, domain)}/div", Tag = scale });
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
        // ReprocessAsync redraws once with the new view, so only the state is reset here.
        if (Math.Abs(previous.TimePerDivisionSeconds - _settings.TimePerDivisionSeconds) > 1e-15)
        {
            _cursorA = _cursorB = null;
            CursorText.Text = CursorReadout.Hint;
            UpdateButtons();
        }

        _ = ReprocessAsync();
    }

    private void UpdateSettingLabels()
    {
        TriggerPositionLabel.Content = $"_Triggerposition: {_settings.TriggerPositionPercent:0} %";
        TriggerLevelLabel.Content = $"_Pegel: {DisplayFormat.Value(_settings.TriggerLevel, _domain)}";
        Ch1CenterLabel.Content = $"CH1 _Mitte: {DisplayFormat.Value(_settings.Channel1.CenterValue, _domain)}";
        Ch2CenterLabel.Content = $"CH2 M_itte: {DisplayFormat.Value(_settings.Channel2.CenterValue, _domain)}";
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
                    DrawPlots();
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
}
