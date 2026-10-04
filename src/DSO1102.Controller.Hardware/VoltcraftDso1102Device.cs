using DSO1102.Controller.Core.Abstractions;
using DSO1102.Controller.Core.Models;

namespace DSO1102.Controller.Hardware;

public sealed class VoltcraftDso1102Device : IDsoDevice
{
    public DeviceInfo DeviceInfo { get; } = new(
        "Voltcraft DSO-1102-USB",
        Dso1102Identity.ObservedManufacturer,
        Dso1102Identity.VendorId,
        Dso1102Identity.ProductId,
        Dso1102Identity.FriendlyName);

    public bool IsConnected => false;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => throw BackendNotReady();

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task ApplySettingsAsync(DsoSettings settings, CancellationToken cancellationToken = default)
        => throw BackendNotReady();

    public ValueTask<AcquisitionFrame> AcquireAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromException<AcquisitionFrame>(BackendNotReady());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static NotSupportedException BackendNotReady() => new(
        "DSO-1102 detected as VID 04B5 / PID 1102, but its installed driver/API and protocol are not verified yet. " +
        "No guessed USB command will be sent.");
}
