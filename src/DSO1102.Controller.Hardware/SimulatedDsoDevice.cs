using DSO1102.Controller.Core.Abstractions;
using DSO1102.Controller.Core.Models;

namespace DSO1102.Controller.Hardware;

public sealed class SimulatedDsoDevice : IDsoDevice
{
    private DsoSettings _settings = new();
    private double _phase;

    public DeviceInfo DeviceInfo { get; } =
        new("DSO-1102 Simulator", "DSO1102 Controller", Dso1102Identity.VendorId, Dso1102Identity.ProductId);

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

        if (settings.SampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings));

        if (settings.RecordLength < 32)
            throw new ArgumentOutOfRangeException(nameof(settings));

        _settings = settings;
        return Task.CompletedTask;
    }

    public ValueTask<AcquisitionFrame> AcquireAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsConnected)
            throw new InvalidOperationException("Simulator is not connected.");

        var ch1 = new double[_settings.RecordLength];
        var ch2 = new double[_settings.RecordLength];

        for (var i = 0; i < ch1.Length; i++)
        {
            var t = i / _settings.SampleRate;
            ch1[i] = 1.8 * Math.Sin(2 * Math.PI * 1000 * t + _phase)
                   + 0.025 * Math.Sin(2 * Math.PI * 17300 * t);
            ch2[i] = 1.2 * Math.Sin(2 * Math.PI * 1000 * t + _phase + Math.PI / 3)
                   + 0.15
                   + 0.018 * Math.Sin(2 * Math.PI * 13700 * t);
        }

        _phase = (_phase + 0.08) % (2 * Math.PI);

        return ValueTask.FromResult(new AcquisitionFrame(
            DateTimeOffset.Now,
            _settings.SampleRate,
            ch1,
            ch2));
    }

    public async ValueTask DisposeAsync()
    {
        if (IsConnected)
            await DisconnectAsync();
    }
}
