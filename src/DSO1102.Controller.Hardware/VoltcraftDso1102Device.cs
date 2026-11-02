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
        _settings = settings;
        return Task.CompletedTask;
    }

    public async ValueTask<AcquisitionFrame> AcquireAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsConnected || _bridge is null)
            throw new InvalidOperationException("DSO-1102 is not connected.");

        ValidateSupportedProfile(_settings);

        using var document = await _bridge.RunAsync("frame-1ms-adc", cancellationToken)
            .ConfigureAwait(false);

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
