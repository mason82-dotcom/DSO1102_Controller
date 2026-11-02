using System.Numerics;

namespace DSO1102.Controller.Core.SignalAnalysis;

public enum WindowFunction { Rectangular, Hann, Hamming, Blackman }
public sealed record FrequencyBin(double FrequencyHz, double Amplitude);

public static class FftAnalyzer
{
    public static IReadOnlyList<FrequencyBin> Analyze(
        IReadOnlyList<double> samples,
        double sampleRate,
        WindowFunction window = WindowFunction.Hann,
        bool removeMean = false)
    {
        if (samples.Count < 2 || !double.IsFinite(sampleRate) || sampleRate <= 0)
            return Array.Empty<FrequencyBin>();

        var n = HighestPowerOfTwo(samples.Count);
        var data = new Complex[n];

        var mean = removeMean ? samples.Take(n).Average() : 0;
        var windowSum = 0.0;
        for (var i = 0; i < n; i++)
        {
            if (!double.IsFinite(samples[i]))
                throw new ArgumentException("FFT samples must be finite.", nameof(samples));
            var weight = WindowValue(window, i, n);
            windowSum += weight;
            data[i] = new Complex((samples[i] - mean) * weight, 0);
        }

        TransformInPlace(data);

        var bins = new FrequencyBin[n / 2 + 1];
        for (var i = 0; i < bins.Length; i++)
            bins[i] = new FrequencyBin(i * sampleRate / n, (i == 0 || i == n / 2 ? 1.0 : 2.0) * data[i].Magnitude / windowSum);

        return bins;
    }

    private static int HighestPowerOfTwo(int value)
    {
        var n = 1;
        while (n <= value / 2)
            n <<= 1;
        return n;
    }

    private static double WindowValue(WindowFunction window, int i, int n)
    {
        if (n <= 1)
            return 1;

        var p = 2.0 * Math.PI * i / n;

        return window switch
        {
            WindowFunction.Hann => 0.5 - 0.5 * Math.Cos(p),
            WindowFunction.Hamming => 0.54 - 0.46 * Math.Cos(p),
            WindowFunction.Blackman => 0.42 - 0.5 * Math.Cos(p) + 0.08 * Math.Cos(2 * p),
            _ => 1.0
        };
    }

    private static void TransformInPlace(Complex[] data)
    {
        var n = data.Length;

        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;

            if (i < j)
                (data[i], data[j]) = (data[j], data[i]);
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2.0 * Math.PI / length;
            var wLength = new Complex(Math.Cos(angle), Math.Sin(angle));

            for (var i = 0; i < n; i += length)
            {
                var w = Complex.One;
                var half = length >> 1;

                for (var j = 0; j < half; j++)
                {
                    var even = data[i + j];
                    var odd = data[i + j + half] * w;
                    data[i + j] = even + odd;
                    data[i + j + half] = even - odd;
                    w *= wLength;
                }
            }
        }
    }
}
