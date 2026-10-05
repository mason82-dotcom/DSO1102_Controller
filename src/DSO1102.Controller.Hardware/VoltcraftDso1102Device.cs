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
        var channel1 = DecodeChannel(
            payload.GetProperty("channel1").GetString(),
            declaredCount);
        var channel2 = DecodeChannel(
            payload.GetProperty("channel2").GetString(),
            declaredCount);

        var requestedCount = Math.Clamp(_settings.RecordLength, 32, declaredCount);
        var reduced1 = Decimate(channel1, requestedCount, out var step1);
        var reduced2 = Decimate(channel2, requestedCount, out var step2);

        var sourceStep = Math.Max(step1, step2);
        var effectiveRate = sourceStep > 0 ? sourceRate / sourceStep : sourceRate;

        return new AcquisitionFrame(
            DateTimeOffset.Now,
            effectiveRate,
            reduced1,
            reduced2)
        {
            SampleDomain = SampleDomain.AdcCounts,
            IsAmplitudeCalibrated = false
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_bridge is not null || IsConnected)
            await DisconnectAsync().ConfigureAwait(false);
    }

    private static void ValidateSupportedProfile(DsoSettings settings)
    {
        if (Math.Abs(settings.TimePerDivisionSeconds - VerifiedTimePerDivisionSeconds) > 1e-12)
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
                    value > 0)
                {
                    return value;
                }
            }
        }

        throw new InvalidOperationException(
            "The bridge frame did not contain a valid decoded-output sample rate.");
    }

    private static double[] DecodeChannel(string? base64, int declaredCount)
    {
        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidOperationException("The bridge frame contains an empty ADC payload.");

        var bytes = Convert.FromBase64String(base64);
        if ((bytes.Length & 1) != 0)
            throw new InvalidOperationException("The bridge ADC payload has an odd byte length.");

        var available = bytes.Length / sizeof(ushort);
        if (declaredCount <= 0 || available < declaredCount)
        {
            throw new InvalidOperationException(
                $"ADC payload length mismatch. Declared={declaredCount}, available={available}.");
        }

        var raw = new ushort[declaredCount];
        Buffer.BlockCopy(bytes, 0, raw, 0, declaredCount * sizeof(ushort));

        var firstValid = Array.FindIndex(raw, value => value <= 0x00FF);
        if (firstValid < 0)
            throw new InvalidOperationException("ADC payload contains no valid 8-bit samples.");

        var fallback = (double)raw[firstValid];
        var result = new double[declaredCount];
        var previous = fallback;

        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] <= 0x00FF)
                previous = raw[i];

            result[i] = previous;
        }

        return result;
    }

    private static double[] Decimate(double[] source, int targetCount, out double sourceStep)
    {
        if (targetCount >= source.Length)
        {
            sourceStep = 1.0;
            return source;
        }

        var result = new double[targetCount];
        sourceStep = (source.Length - 1.0) / (targetCount - 1.0);

        for (var i = 0; i < targetCount; i++)
        {
            var sourceIndex = Math.Min(
                source.Length - 1,
                (int)Math.Round(i * sourceStep));

            result[i] = source[sourceIndex];
        }

        return result;
    }
}
