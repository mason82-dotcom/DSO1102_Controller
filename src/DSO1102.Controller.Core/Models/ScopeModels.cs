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
public enum SampleDomain { Volts, AdcCounts }

/// <summary>Input coupling. Ground is a software display mode, as in the vendor application.</summary>
public enum ChannelCoupling { DC, AC, Ground }

/// <summary>
/// Nominal ADC-to-volt mapping of the Hantek/Voltcraft family: 256 codes over eight
/// vertical divisions around the centred position 128. Not a per-unit calibration.
/// </summary>
public static class NominalAdcScale
{
    public const double CenterCode = 128;
    public const double CodesPerDivision = 32;

    public static double ToVolts(double code, double voltsPerDivision, double probeFactor) =>
        (code - CenterCode) / CodesPerDivision * voltsPerDivision * probeFactor;
}

public sealed record DsoSettings
{
    public double SampleRate { get; init; } = 100_000;
    public int RecordLength { get; init; } = 4096;
    public double TimePerDivisionSeconds { get; init; } = 0.001;
    public bool Channel1Enabled { get; init; } = true;
    public bool Channel2Enabled { get; init; } = true;
    public double Channel1VoltsPerDivision { get; init; } = 1.0;
    public double Channel2VoltsPerDivision { get; init; } = 1.0;
    public TriggerMode TriggerMode { get; init; } = TriggerMode.Auto;
    public TriggerSource TriggerSource { get; init; } = TriggerSource.Channel1;
    public TriggerSlope TriggerSlope { get; init; } = TriggerSlope.Rising;
    public double TriggerLevelVolts { get; init; }

    /// <summary>
    /// Opt-in: let the controller set V/div and coupling through the guarded transient
    /// analog sequence. Off by default because it is not yet verified on the device.
    /// </summary>
    public bool ExperimentalAnalogControl { get; init; }
    public ChannelCoupling Channel1Coupling { get; init; } = ChannelCoupling.DC;
    public ChannelCoupling Channel2Coupling { get; init; } = ChannelCoupling.DC;
    public double Channel1ProbeFactor { get; init; } = 1;
    public double Channel2ProbeFactor { get; init; } = 1;
}

public sealed record AcquisitionFrame(
    DateTimeOffset Timestamp,
    double SampleRate,
    IReadOnlyList<double> Channel1,
    IReadOnlyList<double> Channel2)
{
    public int ReplacedSamplesChannel1 { get; init; }
    public int ReplacedSamplesChannel2 { get; init; }
    public int SampleCount => Math.Min(Channel1.Count, Channel2.Count);
    public SampleDomain SampleDomain { get; init; } = SampleDomain.Volts;
    public bool IsAmplitudeCalibrated { get; init; } = true;

    /// <summary>Volts derived from the nominal ADC mapping, not from a calibration.</summary>
    public bool IsNominalScale { get; init; }
}
