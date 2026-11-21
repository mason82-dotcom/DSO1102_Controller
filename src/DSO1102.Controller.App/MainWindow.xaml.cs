using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using DSO1102.Controller.Core.Abstractions;
using DSO1102.Controller.Core.Models;
using DSO1102.Controller.Core.Presentation;
using DSO1102.Controller.Core.Scope;
using DSO1102.Controller.Core.SignalAnalysis;
using DSO1102.Controller.Hardware;

namespace DSO1102.Controller.App;

// Window lifecycle, device connection and the acquisition loop.
public partial class MainWindow
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

        _ch1Readouts = BuildReadouts(Ch1Readouts, "CH1");
        _ch2Readouts = BuildReadouts(Ch2Readouts, "CH2");

        foreach (var window in Enum.GetValues<WindowFunction>())
            FftWindowCombo.Items.Add(new ComboBoxItem { Content = DisplayFormat.Window(window), Tag = window });

        ConfigureDomainControls(_domain);
        ApplySettingsToControls();
        UpdateButtons();
        Closing += MainWindow_Closing;
    }

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
            StatusText.Text = $"Warte auf Trigger ({DisplayFormat.Source(settings.TriggerSource)} {DisplayFormat.Slope(settings.TriggerSlope)} bei {DisplayFormat.Value(settings.TriggerLevel, _domain)}) — {_framesWithoutTrigger} Aufnahme(n) ohne Flanke.";
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
        // Either the connect actions or "Trennen" are shown, never both: keeps the app bar
        // within the minimum window width and makes the next step obvious.
        var hasDevice = _device is not null;
        ConnectSimulatorButton.Visibility = ConnectHardwareButton.Visibility = hasDevice ? Visibility.Collapsed : Visibility.Visible;
        DisconnectButton.Visibility = hasDevice ? Visibility.Visible : Visibility.Collapsed;
        ConnectSimulatorButton.IsEnabled = ConnectHardwareButton.IsEnabled = idle;
        RunButton.IsEnabled = SingleButton.IsEnabled = idle && connected;
        StopButton.IsEnabled = !idle && !_closing;
        DisconnectButton.IsEnabled = !_closing && hasDevice;
        var hasView = _view is not null;
        ExportWindowCsvButton.IsEnabled = ExportRecordCsvButton.IsEnabled = ExportPngButton.IsEnabled = hasView && !_closing;
        ClearCursorsButton.IsEnabled = _cursorA is not null || _cursorB is not null;
        UpdateStateIndicators();
        Dispatcher.BeginInvoke(RestoreKeyboardFocus, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// Buttons are hidden or disabled as the state changes; if that removed the keyboard
    /// focus, move it to the next useful action instead of leaving it nowhere.
    /// </summary>
    private void RestoreKeyboardFocus()
    {
        if (!IsActive || Keyboard.FocusedElement is UIElement { IsVisible: true, IsEnabled: true })
            return;

        foreach (var candidate in new UIElement[] { RunButton, StopButton, DisconnectButton, ConnectHardwareButton, ConnectSimulatorButton })
        {
            if (candidate is { IsVisible: true, IsEnabled: true } && candidate.Focus())
                return;
        }
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
                else if (_cursorA is not null || _cursorB is not null) ClearCursors();
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
}
