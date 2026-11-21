using System.Text.Json;
using DSO1102.Controller.Core.Abstractions;
using DSO1102.Controller.Core.Models;

namespace DSO1102.Controller.Hardware;

public sealed class VoltcraftDso1102Device : IDsoDevice
{
    private const double VerifiedTimePerDivisionSeconds = 0.001;

    private readonly string? _bridgeExe;
    private readonly string? _vendorDll;
    private BridgeProcessClient? _bridge;
    private DsoSettings _settings = new();
    private string? _appliedAnalogCommand;

    public VoltcraftDso1102Device(string? bridgeExe = null, string? dllPath = null)
    {
        _bridgeExe = bridgeExe;
        _vendorDll = dllPath;
    }

    public DeviceInfo DeviceInfo { get; private set; } = new(
        "Voltcraft DSO-1102-USB",
        Dso1102Identity.ObservedManufacturer,
        Dso1102Identity.VendorId,
        Dso1102Identity.ProductId,
        Dso1102Identity.FriendlyName);

    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsConnected)
            return;

        var bridge = new BridgeProcessClient(_bridgeExe, _vendorDll);

        try
        {
            using var probe = await bridge.RunAsync("probe", cancellationToken).ConfigureAwait(false);
            var root = probe.RootElement;

            if (!root.TryGetProperty("devices", out var devices) ||
                devices.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "DSO1102 bridge probe did not return a devices array.");
            }

            JsonElement? connected = null;
            foreach (var item in devices.EnumerateArray())
            {
                if (item.TryGetProperty("present", out var present) &&
                    present.ValueKind == JsonValueKind.True)
                {
                    connected = item;
                    break;
                }
            }

            if (!connected.HasValue)
            {
                throw new InvalidOperationException(
                    "No DSO-1102 device was found by the original vendor DLL.");
            }

            string? firmware = null;
            if (connected.Value.TryGetProperty("fpgaVersion", out var fpga) &&
                fpga.ValueKind == JsonValueKind.Number &&
                fpga.TryGetInt32(out var fpgaVersion))
            {
                firmware = $"FPGA {fpgaVersion}";
            }

            _bridge = bridge;
            DeviceInfo = DeviceInfo with { Firmware = firmware };
            _appliedAnalogCommand = null; // device state unknown after (re)connect
            IsConnected = true;
        }
        catch
        {
            await bridge.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var bridge = _bridge;
        _bridge = null;
        IsConnected = false;

        if (bridge is not null)
            await bridge.DisposeAsync().ConfigureAwait(false);
    }

    public Task ApplySettingsAsync(
        DsoSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsConnected)
            throw new InvalidOperationException("DSO-1102 is not connected.");

        ValidateSupportedProfile(settings);
        if (settings.ExperimentalAnalogControl)
            _ = BuildAnalogCommand(settings); // validates V/div, coupling and probe factor
        _settings = settings;
        return Task.CompletedTask;
    }

    /// <summary>
    /// V/div values whose vendor range codes were captured in runtime traces of the
    /// original application (docs/REVERSE_ENGINEERING.md, "Voltage/div and coupling runtime mapping").
    /// </summary>
    public static IReadOnlyList<double> SupportedVoltsPerDivision { get; } = [0.5, 1.0, 2.0];

    internal static string BuildAnalogCommand(DsoSettings settings)
    {
        static int RangeCode(double voltsPerDivision) => voltsPerDivision switch
        {
            0.5 => 5,
            1.0 => 6,
            2.0 => 7,
            _ => throw new NotSupportedException(
                $"{voltsPerDivision} V/div is not runtime-verified. Supported: 500 mV/div, 1 V/div, 2 V/div.")
        };

        // Ground uses the non-AC relay state; the samples are replaced in software.
        static int CouplingCode(ChannelCoupling coupling) => coupling == ChannelCoupling.AC ? 1 : 0;

        foreach (var factor in new[] { settings.Channel1ProbeFactor, settings.Channel2ProbeFactor })
        {
            if (!double.IsFinite(factor) || factor <= 0)
                throw new ArgumentOutOfRangeException(nameof(settings), "Probe factor must be positive.");
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"frame-1ms-analog {RangeCode(settings.Channel1VoltsPerDivision)} {RangeCode(settings.Channel2VoltsPerDivision)} " +
            $"{CouplingCode(settings.Channel1Coupling)} {CouplingCode(settings.Channel2Coupling)}");
    }

    public async ValueTask<AcquisitionFrame> AcquireAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsConnected || _bridge is null)
            throw new InvalidOperationException("DSO-1102 is not connected.");

        var settings = _settings;
        ValidateSupportedProfile(settings);

        // The analog sequence runs only when the requested front-end state differs from
        // the one this session last applied; afterwards the plain frame command is used.
        string? analogCommand = null;
        if (settings.ExperimentalAnalogControl)
        {
            var requested = BuildAnalogCommand(settings);
            if (!string.Equals(requested, _appliedAnalogCommand, StringComparison.Ordinal))
                analogCommand = requested;
        }

        using var document = await _bridge.RunAsync(analogCommand ?? "frame-1ms-adc", cancellationToken)
            .ConfigureAwait(false);

        if (analogCommand is not null)
            _appliedAnalogCommand = analogCommand;

        var root = document.RootElement;
        var sourceRate = ReadDecodedOutputRate(root);

        if (!root.TryGetProperty("capture", out var capture) ||
            !capture.TryGetProperty("adcPayload", out var payload) ||
            payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidOperationException(
                "The bridge capture did not contain an ADC payload.");
        }

        var encoding = payload.GetProperty("encoding").GetString();
        if (!string.Equals(encoding, "u16le-base64", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported bridge ADC encoding '{encoding}'.");

        var declaredCount = payload.GetProperty("sampleCountPerChannel").GetInt32();
        var channel1 = AdcPayloadDecoder.Decode(
            payload.GetProperty("channel1").GetString(),
            declaredCount);
        var channel2 = AdcPayloadDecoder.Decode(
            payload.GetProperty("channel2").GetString(),
            declaredCount);

        if (settings.ExperimentalAnalogControl && _appliedAnalogCommand is not null)
        {
            return new AcquisitionFrame(
                DateTimeOffset.Now,
                sourceRate,
                ToNominalVolts(channel1.Samples, settings.Channel1VoltsPerDivision, settings.Channel1ProbeFactor, settings.Channel1Coupling),
                ToNominalVolts(channel2.Samples, settings.Channel2VoltsPerDivision, settings.Channel2ProbeFactor, settings.Channel2Coupling))
            {
                SampleDomain = SampleDomain.Volts,
                IsAmplitudeCalibrated = false,
                IsNominalScale = true,
                ReplacedSamplesChannel1 = channel1.ReplacedCount,
                ReplacedSamplesChannel2 = channel2.ReplacedCount
            };
        }

        return new AcquisitionFrame(
            DateTimeOffset.Now,
            sourceRate,
            channel1.Samples,
            channel2.Samples)
        {
            SampleDomain = SampleDomain.AdcCounts,
            IsAmplitudeCalibrated = false,
            ReplacedSamplesChannel1 = channel1.ReplacedCount,
            ReplacedSamplesChannel2 = channel2.ReplacedCount
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_bridge is not null || IsConnected)
            await DisconnectAsync().ConfigureAwait(false);
    }

    internal static double[] ToNominalVolts(double[] codes, double voltsPerDivision, double probeFactor, ChannelCoupling coupling)
    {
        var result = new double[codes.Length];
        if (coupling == ChannelCoupling.Ground)
            return result; // software GND: flat reference line at 0 V, as in the vendor UI

        for (var i = 0; i < codes.Length; i++)
            result[i] = NominalAdcScale.ToVolts(codes[i], voltsPerDivision, probeFactor);
        return result;
    }

    private static void ValidateSupportedProfile(DsoSettings settings)
    {
        if (!double.IsFinite(settings.TimePerDivisionSeconds) || Math.Abs(settings.TimePerDivisionSeconds - VerifiedTimePerDivisionSeconds) > 1e-12)
        {
            throw new NotSupportedException(
                "The first real-hardware backend currently exposes only the runtime-verified 1 ms/div profile.");
        }

        if (!settings.Channel1Enabled || !settings.Channel2Enabled)
        {
            throw new NotSupportedException(
                "The current verified 1 ms/div backend requires both CH1 and CH2 enabled.");
        }

        if (settings.TriggerSource != TriggerSource.Channel1)
        {
            throw new NotSupportedException(
                "The current verified frame command uses CH1 as the trigger source.");
        }

        if (settings.TriggerSlope != TriggerSlope.Rising || settings.TriggerMode != TriggerMode.Auto ||
            settings.TriggerLevelVolts != 0)
            throw new NotSupportedException("The verified capture is forced-trigger only; programmable trigger mode, slope and voltage level are not exposed.");

        if (settings.RecordLength < 32)
            throw new ArgumentOutOfRangeException(nameof(settings), "RecordLength must be at least 32.");

        if (settings.RecordLength > 524_288)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "The verified two-channel deep-memory record contains at most 524,288 samples/channel.");
        }
    }

    private static double ReadDecodedOutputRate(JsonElement root)
    {
        if (root.TryGetProperty("profile", out var profile))
        {
            foreach (var name in new[]
                     {
                         "calReferencedEffectiveAcquisitionRateHz",
                         "decodedOutputReferenceRateHz"
                     })
            {
                if (profile.TryGetProperty(name, out var rate) &&
                    rate.ValueKind == JsonValueKind.Number &&
                    rate.TryGetDouble(out var value) &&
                    double.IsFinite(value) && value > 0)
                {
                    return value;
                }
            }
        }

        throw new InvalidOperationException(
            "The bridge frame did not contain a valid decoded-output sample rate.");
    }

}
