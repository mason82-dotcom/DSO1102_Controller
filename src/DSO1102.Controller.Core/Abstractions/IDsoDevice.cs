using DSO1102.Controller.Core.Models;

namespace DSO1102.Controller.Core.Abstractions;

public interface IDsoDevice : IAsyncDisposable
{
    DeviceInfo DeviceInfo { get; }
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task ApplySettingsAsync(DsoSettings settings, CancellationToken cancellationToken = default);
    ValueTask<AcquisitionFrame> AcquireAsync(CancellationToken cancellationToken = default);
}
