using System.Text.Json;
using DSO1102.Controller.Core.Abstractions;
using DSO1102.Controller.Core.Models;

namespace DSO1102.Controller.Hardware;

public sealed class VoltcraftDso1102Device : IDsoDevice
{
    private const double VerifiedTimePerDivisionSeconds = 0.001;
    private const double VerifiedDecodedStreamRateHz = 5_000_000.0;
    private const int VerifiedDeepRecordSamples = 524_288;

    private readonly Dso1102BridgeClient _bridge;
    private DsoSettings _settings = new()
    {
        TimePerDivisionSeconds = VerifiedTimePerDivisionSeconds,
        SampleRate = VerifiedDecodedStreamRateHz,
        RecordLength = VerifiedDeepRecordSamples,
        Channel1Enabled = true,
        Channel2Enabled = true,
        Channel1VoltsPerDivision = 1.0,
        Channel2VoltsPerDivision = 1.0,
        TriggerSource = TriggerSource.Channel1,
        TriggerSlope = TriggerSlope.Rising,
        TriggerMode = TriggerMode.Auto
    };

    private DeviceInfo _deviceInfo = new(
        "Voltcraft DSO-1102-USB",
        Dso1102Identity.ObservedManufacturer,
        Dso1102Identity.VendorId,
        Dso1102Identity.ProductId,
        Dso1102Identity.FriendlyName);

    public VoltcraftDso1102Device(string? bridgeExe = null, string? dllPath = null)
    {
        _bridge = new Dso1102BridgeClient(bridgeExe, dllPath);
    }

    public DeviceInfo DeviceInfo => _deviceInfo;

    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var result = await _bridge.RunAsync("probe", cancellationToken).ConfigureAwait(false);
        var root = result.RootElement;

        if (!root.TryGetProperty("devices", out var devices) ||
            devices.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The x86 bridge returned no device list.");
        }

        JsonElement? connected = null;

        foreach (var device in devices.EnumerateArray())
        {
            if (device.TryGetProperty("present", out var present) && present.GetBoolean())
            {
                connected = device;
                break;
            }
        }

        if (!connected.HasValue)
            throw new InvalidOperationException("No DSO-1102 device was found at bridge indices 0..3.");

        string? firmware = null;
        if (connected.Value.TryGetProperty("fpgaVersion", out var fpga) &&
            fpga.ValueKind == JsonValueKind.Number)
        {
            firmware = $"FPGA {fpga.GetInt32()}";
        }

        _deviceInfo = _deviceInfo with { Firmware = firmware };
        IsConnected = true;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task ApplySettingsAsync(
        DsoSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsConnected)
            throw new InvalidOperationException("DSO-1102 is not connected.");

        if (Math.Abs(settings.TimePerDivisionSeconds - VerifiedTimePerDivisionSeconds) > 1e-12)
        {
            throw new NotSupportedException(
                "The first real-hardware backend currently exposes only the runtime-verified 1 ms/div profile.");
        }

        if (!settings.Channel1Enabled || !settings.Channel2Enabled)
        {
            throw new NotSupportedException(
                "The verified normal acquisition path currently requires CH1 and CH2 enabled.");
        }

        if (settings.TriggerSource != TriggerSource.Channel1)
        {
            throw new NotSupportedException(
                "The first real-hardware backend currently uses the verified CH1 trigger-source profile.");
        }

        if (Math.Abs(settings.Channel1VoltsPerDivision - 1.0) > 1e-12 ||
            Math.Abs(settings.Channel2VoltsPerDivision - 1.0) > 1e-12)
        {
            throw new NotSupportedException(
                "The UI/backend contract is currently limited to the 1 V/div nominal profile. " +
                "The bridge does not yet automatically apply analog V/div settings during normal acquisition.");
        }

        if (settings.RecordLength is < 32 or > VerifiedDeepRecordSamples)
            throw new ArgumentOutOfRangeException(nameof(settings.RecordLength));

        _settings = settings with
        {
            SampleRate = VerifiedDecodedStreamRateHz
        };

        return Task.CompletedTask;
    }

    public async ValueTask<AcquisitionFrame> AcquireAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsConnected)
            throw new InvalidOperationException("DSO-1102 is not connected.");

        using var result = await _bridge.RunAsync("frame-1ms-adc", cancellationToken)
            .ConfigureAwait(false);

        var root = result.RootElement;

        if (!root.TryGetProperty("capture", out var capture) ||
            !capture.TryGetProperty("adcPayload", out var payload) ||
            payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("Bridge capture did not contain an ADC payload.");
        }

        var encoding = payload.GetProperty("encoding").GetString();
        if (!string.Equals(encoding, "u16le-base64", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported bridge ADC encoding '{encoding}'.");

        var sampleCount = payload.GetProperty("sampleCountPerChannel").GetInt32();
        var channel1 = DecodeAdcPayload(payload.GetProperty("channel1").GetString(), sampleCount);
        var channel2 = DecodeAdcPayload(payload.GetProperty("channel2").GetString(), sampleCount);

        var effectiveRate = VerifiedDecodedStreamRateHz;
        if (root.TryGetProperty("profile", out var profile) &&
            profile.TryGetProperty("decodedOutputReferenceRateHz", out var rateElement) &&
            rateElement.ValueKind == JsonValueKind.Number)
        {
            effectiveRate = rateElement.GetDouble();
        }

        var outputCount = Math.Min(
            Math.Min(channel1.Length, channel2.Length),
            _settings.RecordLength);

        if (outputCount <= 0)
            throw new InvalidOperationException("Bridge returned an empty waveform.");

        if (outputCount != channel1.Length)
            Array.Resize(ref channel1, outputCount);

        if (outputCount != channel2.Length)
            Array.Resize(ref channel2, outputCount);

        SanitizeAdcSentinels(channel1);
        SanitizeAdcSentinels(channel2);

        return new AcquisitionFrame(
            DateTimeOffset.Now,
            effectiveRate,
            channel1,
            channel2)
        {
            SampleDomain = SampleDomain.AdcCounts,
            IsAmplitudeCalibrated = false
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (IsConnected)
            await DisconnectAsync().ConfigureAwait(false);
    }

    private static double[] DecodeAdcPayload(string? base64, int expectedSamples)
    {
        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidOperationException("Bridge ADC payload is empty.");

        var bytes = Convert.FromBase64String(base64);

        if ((bytes.Length & 1) != 0)
            throw new InvalidOperationException("Bridge ADC payload has an odd byte length.");

        var actualSamples = bytes.Length / sizeof(ushort);
        if (expectedSamples > 0 && actualSamples != expectedSamples)
        {
            throw new InvalidOperationException(
                $"Bridge ADC payload sample count mismatch. Expected {expectedSamples}, got {actualSamples}.");
        }

        var result = new double[actualSamples];

        for (var i = 0; i < actualSamples; i++)
            result[i] = BitConverter.ToUInt16(bytes, i * sizeof(ushort));

        return result;
    }

    private static void SanitizeAdcSentinels(double[] samples)
    {
        if (samples.Length == 0)
            return;

        for (var i = 0; i < samples.Length; i++)
        {
            if (samples[i] is >= 0 and <= 255)
                continue;

            if (i > 0 && samples[i - 1] is >= 0 and <= 255)
            {
                samples[i] = samples[i - 1];
                continue;
            }

            var replacement = 128.0;
            for (var j = i + 1; j < samples.Length; j++)
            {
                if (samples[j] is >= 0 and <= 255)
                {
                    replacement = samples[j];
                    break;
                }
            }

            samples[i] = replacement;
        }
    }
}
