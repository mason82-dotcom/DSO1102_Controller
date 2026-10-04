namespace DSO1102.Controller.Core.Models;

public sealed record DeviceInfo(
    string Name,
    string Manufacturer,
    ushort VendorId,
    ushort ProductId,
    string? Driver = null,
    string? Firmware = null);

public enum TriggerMode { Auto, Normal, Single }
public enum TriggerSlope { Rising, Falling }
public enum TriggerSource { Channel1, Channel2, External }

public sealed record DsoSettings
{
    public double SampleRate { get; init; } = 100_000;
    public int RecordLength { get; init; } = 4096;
    public bool Channel1Enabled { get; init; } = true;
    public bool Channel2Enabled { get; init; } = true;
    public double Channel1VoltsPerDivision { get; init; } = 1.0;
    public double Channel2VoltsPerDivision { get; init; } = 1.0;
    public TriggerMode TriggerMode { get; init; } = TriggerMode.Auto;
    public TriggerSource TriggerSource { get; init; } = TriggerSource.Channel1;
    public TriggerSlope TriggerSlope { get; init; } = TriggerSlope.Rising;
    public double TriggerLevelVolts { get; init; }
}

public sealed record AcquisitionFrame(
    DateTimeOffset Timestamp,
    double SampleRate,
    IReadOnlyList<double> Channel1,
    IReadOnlyList<double> Channel2)
{
    public int SampleCount => Math.Min(Channel1.Count, Channel2.Count);
}
