namespace DSO1102.Controller.Core.SignalAnalysis;

public sealed record SignalMeasurements(
    double Minimum,
    double Maximum,
    double PeakToPeak,
    double Mean,
    double Rms,
    double FrequencyHz,
    double PeriodSeconds);

public static class SignalAnalyzer
{
    public static SignalMeasurements Measure(IReadOnlyList<double> samples, double sampleRate)
    {
        if (samples.Count == 0)
            return new(0, 0, 0, 0, 0, 0, 0);

        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        var sum = 0.0;
        var squares = 0.0;

        foreach (var value in samples)
        {
            min = Math.Min(min, value);
            max = Math.Max(max, value);
            sum += value;
            squares += value * value;
        }

        var mean = sum / samples.Count;
        var rms = Math.Sqrt(squares / samples.Count);
        var frequency = EstimateFrequency(samples, sampleRate, mean);

        return new(
            min,
            max,
            max - min,
            mean,
            rms,
            frequency,
            frequency > 0 ? 1.0 / frequency : 0);
    }

    private static double EstimateFrequency(IReadOnlyList<double> samples, double sampleRate, double mean)
    {
        if (sampleRate <= 0 || samples.Count < 3)
            return 0;

        var crossings = new List<double>();

        for (var i = 1; i < samples.Count; i++)
        {
            var a = samples[i - 1] - mean;
            var b = samples[i] - mean;

            if (a <= 0 && b > 0)
            {
                var denominator = b - a;
                var fraction = Math.Abs(denominator) > double.Epsilon ? -a / denominator : 0;
                crossings.Add(i - 1 + fraction);
            }
        }

        if (crossings.Count < 2)
            return 0;

        var sum = 0.0;
        for (var i = 1; i < crossings.Count; i++)
            sum += crossings[i] - crossings[i - 1];

        var averagePeriodSamples = sum / (crossings.Count - 1);
        return averagePeriodSamples > 0 ? sampleRate / averagePeriodSamples : 0;
    }
}
