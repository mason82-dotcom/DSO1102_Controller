using DSO1102.Controller.Core.Abstractions;
using DSO1102.Controller.Core.Models;

namespace DSO1102.Controller.Hardware;

/// <summary>
/// Development source shaped like the real backend: long records, a 1 kHz CAL-like
/// square wave on CH1 and a sine on CH2, random phase per frame and a little noise,
/// so the software trigger and time base can be exercised without hardware.
/// Values are in volts and therefore marked as a different domain than real ADC data.
/// </summary>
public sealed class SimulatedDsoDevice : IDsoDevice
{
    public const double Channel1FrequencyHz = 1_000;
    public const double Channel2FrequencyHz = 2_500;

    private readonly Random _random;
    private DsoSettings _settings = new();

    public SimulatedDsoDevice(int? seed = null)
    {
        _random = seed is { } s ? new Random(s) : new Random();
    }

    public DeviceInfo DeviceInfo { get; } =
        new("DSO-1102 Simulator", "DSO1102 Controller", Dso1102Identity.VendorId, Dso1102Identity.ProductId,
            Firmware: "Simulation");

    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task ApplySettingsAsync(DsoSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!double.IsFinite(settings.SampleRate) || settings.SampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings), "SampleRate must be positive.");

        if (settings.RecordLength is < 32 or > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(settings), "RecordLength must be 32..1048576.");

        _settings = settings;
        return Task.CompletedTask;
    }

    public async ValueTask<AcquisitionFrame> AcquireAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsConnected)
            throw new InvalidOperationException("Simulator is not connected.");

        var settings = _settings;
        var phase1 = _random.NextDouble();
        var phase2 = _random.NextDouble() * 2 * Math.PI;
        var noiseSeed = _random.Next();

        var (ch1, ch2) = await Task.Run(() =>
        {
            var noise = new Random(noiseSeed);
            var a = new double[settings.RecordLength];
            var b = new double[settings.RecordLength];
            var rate = settings.SampleRate;
            // First-order edge rounding similar to a probe/front-end low-pass.
            var alpha = 1 - Math.Exp(-1 / (rate * 2e-6));
            var smoothed = 0.0;

            for (var i = 0; i < a.Length; i++)
            {
                var t = i / rate;
                var cycle = (t * Channel1FrequencyHz + phase1) % 1.0;
                var square = cycle < 0.5 ? 2.0 : 0.0;
                smoothed += alpha * (square - smoothed);
                a[i] = smoothed + 0.02 * (noise.NextDouble() - 0.5);
                b[i] = 1.2 * Math.Sin(2 * Math.PI * Channel2FrequencyHz * t + phase2)
                       + 0.15
                       + 0.03 * (noise.NextDouble() - 0.5);
            }

            return (a, b);
        }, cancellationToken).ConfigureAwait(false);

        if (settings.ExperimentalAnalogControl)
        {
            ApplyFrontEnd(ch1, settings.Channel1Coupling, settings.Channel1ProbeFactor);
            ApplyFrontEnd(ch2, settings.Channel2Coupling, settings.Channel2ProbeFactor);
        }

        // Pace like a real acquisition instead of spinning the UI.
        await Task.Delay(30, cancellationToken).ConfigureAwait(false);

        return new AcquisitionFrame(DateTimeOffset.Now, settings.SampleRate, ch1, ch2);
    }

    /// <summary>Mirrors the hardware path: AC removes the mean, GND is a flat 0 V line.</summary>
    private static void ApplyFrontEnd(double[] samples, ChannelCoupling coupling, double probeFactor)
    {
        if (coupling == ChannelCoupling.Ground)
        {
            Array.Clear(samples);
            return;
        }

        var mean = coupling == ChannelCoupling.AC ? samples.Average() : 0;
        var factor = double.IsFinite(probeFactor) && probeFactor > 0 ? probeFactor : 1;
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (samples[i] - mean) * factor;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsConnected)
            await DisconnectAsync();
    }
}
