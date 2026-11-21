namespace DSO1102.Controller.Core.SignalAnalysis;

public readonly record struct WaveformPoint(int SampleIndex, double Value);

public static class WaveformEnvelope
{
    // Display-only min/max buckets. Never feed these irregular samples to DSP.
    public static IReadOnlyList<WaveformPoint> Create(IReadOnlyList<double> samples, int buckets) =>
        Create(samples, 0, samples.Count, buckets);

    /// <summary>Min/max envelope of samples[start..start+count); indices stay absolute.</summary>
    public static IReadOnlyList<WaveformPoint> Create(IReadOnlyList<double> samples, int start, int count, int buckets)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfLessThan(buckets, 1);
        if (start < 0 || count < 0 || start > samples.Count - count)
            throw new ArgumentOutOfRangeException(nameof(count), "Range is outside the sample list.");

        buckets = Math.Min(buckets, count);
        var points = new List<WaveformPoint>(buckets * 2);
        for (var bucket = 0; bucket < buckets; bucket++)
        {
            var bucketStart = start + (int)((long)bucket * count / buckets);
            var bucketEnd = start + (int)((long)(bucket + 1) * count / buckets);
            var min = bucketStart;
            var max = bucketStart;
            for (var i = bucketStart + 1; i < bucketEnd; i++)
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
