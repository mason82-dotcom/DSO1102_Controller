namespace DSO1102.Controller.Core.SignalAnalysis;

public sealed record SignalMeasurements(
    double Minimum,
    double Maximum,
    double PeakToPeak,
    double Mean,
    double Rms,
    double FrequencyHz,
    double PeriodSeconds)
{
    /// <summary>High time / period of the detected periodic signal, 0..1; NaN if unknown.</summary>
    public double DutyCycle { get; init; } = double.NaN;

    /// <summary>AC RMS (standard deviation) of the analysed samples.</summary>
    public double AcRms { get; init; }

    public static SignalMeasurements Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}

public static class SignalAnalyzer
{
    /// <summary>Hysteresis around the mid level, as fraction of peak-to-peak.</summary>
    public const double FrequencyHysteresisFraction = 0.1;

    public static SignalMeasurements Measure(IReadOnlyList<double> samples, double sampleRate) =>
        Measure(samples, 0, samples.Count, sampleRate);

    /// <param name="minimumHysteresis">
    /// Absolute floor for the frequency hysteresis, e.g. a few ADC codes, so that pure
    /// quantisation noise on an idle channel is not counted as a periodic signal.
    /// </param>
    public static SignalMeasurements Measure(IReadOnlyList<double> samples, int start, int count, double sampleRate, double minimumHysteresis = 0)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (start < 0 || count < 0 || start > samples.Count - count)
            throw new ArgumentOutOfRangeException(nameof(count), "Range is outside the sample list.");

        if (count == 0)
            return SignalMeasurements.Empty;

        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        var sum = 0.0;
        var squares = 0.0;
        var end = start + count;

        for (var i = start; i < end; i++)
        {
            var value = samples[i];
            if (value < min) min = value;
            if (value > max) max = value;
            sum += value;
            squares += value * value;
        }

        var mean = sum / count;
        var rms = Math.Sqrt(squares / count);
        var variance = Math.Max(0, squares / count - mean * mean);
        var (frequency, duty) = EstimateFrequency(samples, start, end, sampleRate, (min + max) / 2, max - min, minimumHysteresis);

        return new(
            min,
            max,
            max - min,
            mean,
            rms,
            frequency,
            frequency > 0 ? 1.0 / frequency : 0)
        {
            DutyCycle = duty,
            AcRms = Math.Sqrt(variance)
        };
    }

    /// <summary>
    /// Counts rising crossings of the mid level with a hysteresis band so that noise on
    /// slow edges does not create extra periods. The crossing point is linearly
    /// interpolated; the period is averaged between the first and last crossing.
    /// </summary>
    private static (double Frequency, double Duty) EstimateFrequency(
        IReadOnlyList<double> samples,
        int start,
        int end,
        double sampleRate,
        double mid,
        double peakToPeak,
        double minimumHysteresis)
    {
        if (!double.IsFinite(sampleRate) || sampleRate <= 0 || end - start < 3 || !(peakToPeak > 0))
            return (0, double.NaN);

        var hysteresis = Math.Max(peakToPeak * FrequencyHysteresisFraction, double.IsFinite(minimumHysteresis) ? minimumHysteresis : 0);
        if (2 * hysteresis >= peakToPeak)
            return (0, double.NaN);
        var low = mid - hysteresis;
        var high = mid + hysteresis;

        double? firstRising = null;
        double lastRising = 0;
        var risingCount = 0;
        var highSamplesInPeriods = 0L;
        var highSamplesCurrent = 0L;

        var state = 0; // -1 low, +1 high, 0 unknown
        var lastBelowMid = -1;

        for (var i = start; i < end; i++)
        {
            var value = samples[i];

            if (value < mid)
                lastBelowMid = i;

            if (state >= 0 && value < low)
            {
                state = -1;
            }
            else if (state <= 0 && value > high)
            {
                if (state == -1 && lastBelowMid >= start && lastBelowMid + 1 < end)
                {
                    var a = samples[lastBelowMid] - mid;
                    var b = samples[lastBelowMid + 1] - mid;
                    var fraction = b - a > double.Epsilon ? -a / (b - a) : 0;
                    var crossing = lastBelowMid + Math.Clamp(fraction, 0, 1);

                    if (firstRising is null)
                    {
                        firstRising = crossing;
                    }
                    else
                    {
                        highSamplesInPeriods += highSamplesCurrent;
                    }

                    highSamplesCurrent = 0;
                    lastRising = crossing;
                    risingCount++;
                }

                state = 1;
            }

            if (firstRising is not null && value >= mid)
                highSamplesCurrent++;
        }

        if (risingCount < 2 || firstRising is null)
            return (0, double.NaN);

        var periods = risingCount - 1;
        var periodSamples = (lastRising - firstRising.Value) / periods;
        if (!(periodSamples > 0))
            return (0, double.NaN);

        var duty = Math.Clamp(highSamplesInPeriods / (periodSamples * periods), 0, 1);
        return (sampleRate / periodSamples, duty);
    }
}
