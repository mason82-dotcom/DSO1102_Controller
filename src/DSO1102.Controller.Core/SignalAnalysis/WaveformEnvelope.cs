namespace DSO1102.Controller.Core.SignalAnalysis;

public readonly record struct WaveformPoint(int SampleIndex, double Value);

public static class WaveformEnvelope
{
    // Display-only min/max buckets. Never feed these irregular samples to DSP.
    public static IReadOnlyList<WaveformPoint> Create(IReadOnlyList<double> samples, int buckets)
    {
        if (buckets < 1) throw new ArgumentOutOfRangeException(nameof(buckets));
        buckets = Math.Min(buckets, samples.Count);
        var points = new List<WaveformPoint>(buckets * 2);
        for (var bucket = 0; bucket < buckets; bucket++)
        {
            var start = (int)((long)bucket * samples.Count / buckets);
            var end = (int)((long)(bucket + 1) * samples.Count / buckets);
            var min = start;
            var max = start;
            for (var i = start + 1; i < end; i++)
            {
                if (samples[i] < samples[min]) min = i;
                if (samples[i] > samples[max]) max = i;
            }
            var first = Math.Min(min, max);
            var last = Math.Max(min, max);
            points.Add(new(first, samples[first]));
            if (last != first) points.Add(new(last, samples[last]));
        }
        return points;
    }
}
