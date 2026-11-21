using System.Runtime.InteropServices;
using System.Text.Json;
using DSO1102.Controller.Core.Models;

namespace DSO1102.Controller.Bridge;

// Host-side analysis of captured buffers for diagnostic JSON. No device access.
internal static partial class Program
{
    private static string TriggerSourceName(ushort code) => code switch
    {
        0 => "CH1",
        1 => "CH2",
        2 => "ALT",
        3 => "EXT",
        4 => "EXT/10",
        _ => $"Unknown ({code})"
    };

    private static object? DescribeHardwareSamplerateProgramming(
        ushort? timeBaseCode,
        ushort recordLengthStateRaw,
        ushort samplerateStateWord6Raw)
    {
        if (!timeBaseCode.HasValue)
            return null;

        var code = timeBaseCode.Value;
        var deepRecordState = recordLengthStateRaw != 0;

        static string? TimeBaseLabel(ushort value) => value switch
        {
            0 => "4 ns/div",
            1 => "10 ns/div",
            2 => "20 ns/div",
            3 => "40 ns/div",
            4 => "100 ns/div",
            5 => "200 ns/div",
            6 => "400 ns/div",
            7 => "1 us/div",
            8 => "2 us/div",
            9 => "4 us/div",
            10 => "10 us/div",
            11 => "20 us/div",
            12 => "40 us/div",
            13 => "100 us/div",
            14 => "200 us/div",
            15 => "400 us/div",
            16 => "1 ms/div",
            17 => "2 ms/div",
            18 => "4 ms/div",
            19 => "10 ms/div",
            20 => "20 ms/div",
            21 => "40 ms/div",
            22 => "100 ms/div",
            23 => "200 ms/div",
            24 => "400 ms/div",
            25 => "1 s/div",
            26 => "2 s/div",
            27 => "4 s/div",
            28 => "10 s/div",
            29 => "20 s/div",
            30 => "40 s/div",
            31 => "1 min/div",
            32 => "2 min/div",
            33 => "4 min/div",
            34 => "10 min/div",
            35 => "20 min/div",
            36 => "40 min/div",
            37 => "1 h/div",
            _ => null
        };

        // Tuple fields are taken directly from the _dsoSetSampleRate@8 jump table.
        // baseControlBits are command-byte-2 bits 0..2 before config.word[6].bit0
        // is inserted as command-byte-2 bit 3.
        (ushort primary, ushort secondary, byte baseControlBits)? programming =
            (code, deepRecordState) switch
            {
                (<= 9, _) => (0x0000, 0x0000, 0x05),
                (10, _) => (0x0000, 0x0000, 0x07),
                (11, _) => (0xFFFF, 0x0001, 0x06),

                (12, false) => (0xFFFD, 0xFFFF, 0x02),
                (12, true) => (0xFFFF, 0xFFFF, 0x07),

                (13, false) => (0xFFF7, 0xFFFC, 0x02),
                (13, true) => (0xFFFD, 0xFFFF, 0x02),

                (14, false) => (0xFFED, 0xFFF7, 0x06),
                (14, true) => (0xFFF7, 0xFFFC, 0x06),

                (15, false) => (0xFFD9, 0xFFED, 0x02),
                (15, true) => (0xFFED, 0xFFF7, 0x02),

                (16, false) => (0xFF9D, 0xFFCF, 0x02),
                (16, true) => (0xFFD9, 0xFFED, 0x02),

                (17, false) => (0xFF39, 0xFF9D, 0x02),
                (17, true) => (0xFF9D, 0xFFCF, 0x02),

                (18, false) => (0xFE71, 0xFF39, 0x02),
                (18, true) => (0xFF39, 0xFF9D, 0x02),

                (19, false) => (0xFC19, 0xFE0D, 0x02),
                (19, true) => (0xFE71, 0xFF39, 0x02),

                (20, false) => (0xF83E, 0xFC19, 0x02),
                (20, true) => (0xFC1A, 0xFE0D, 0x02),

                (21, false) => (0xF06E, 0xF83E, 0x02),
                (21, true) => (0xF83E, 0xFC1A, 0x02),

                (22, false) => (0xD8CC, 0xEC7A, 0x02),
                (22, true) => (0xF06E, 0xF83E, 0x02),

                (23, false) => (0xAFC8, 0xD8CC, 0x02),
                (23, true) => (0xD8CC, 0xD8CC, 0x02),

                (24, false) => (0x639C, 0xD8CC, 0x02),
                (24, true) => (0xB1BC, 0xD8CC, 0x02),

                (25, _) => (0xAFC8, 0xD8CC, 0x02),
                (>= 26 and <= 37, _) => (0xFFED, 0xD8CC, 0x02),
                _ => null
            };

        if (!programming.HasValue)
            return new
            {
                source = "DSO1102USB.dll _dsoSetSampleRate@8",
                staticallyDecoded = false,
                timeBaseCode = code,
                timeBaseLabel = TimeBaseLabel(code),
                note = "Time/DIV code is outside the statically decoded 0..37 switch."
            };

        static int? EncodedDivider(ushort value) =>
            value == 0 ? null : 0x10001 - value;

        var p = programming.Value;
        var primaryDivider = EncodedDivider(p.primary);
        var secondaryMechanicalDivider = EncodedDivider(p.secondary);
        var commandByte2 = (byte)(p.baseControlBits | ((samplerateStateWord6Raw & 1) << 3));

        // OpenHantek's DSO-2250 path independently uses command 0x0E,
        // byte2.bit0=fast-rate, byte2.bit1=downsampling and
        // samplerateWord = 0x10001 - divider. That makes the primary word a
        // strong family-correlated downsampler candidate. The DSO-1102's
        // second timing word has no public DSO-2250 counterpart.
        double? familyCorrelatedRateCandidateHz = null;
        if (deepRecordState &&
            code is >= 13 and <= 24 &&
            primaryDivider is > 0 &&
            (commandByte2 & 0x01) == 0 &&
            (commandByte2 & 0x02) != 0)
        {
            familyCorrelatedRateCandidateHz = 100_000_000.0 / primaryDivider.Value;
        }

        return new
        {
            source = "Direct static disassembly of DSO1102USB.dll _dsoSetSampleRate@8.",
            openHantekCorrelation = "DSO-2250 uses 0x0E, bit0 fast-rate, bit1 downsampling, and samplerateWord = 0x10001 - divider.",
            staticallyDecoded = true,
            runtimeWaveformVerifiedOnThisDso1102 = code is >= 15 and <= 18,
            timeBaseCode = code,
            timeBaseLabel = TimeBaseLabel(code),
            recordStateBranch = deepRecordState ? "config.word[4] != 0" : "config.word[4] == 0",
            recordLengthStateRaw,
            samplerateStateWord6Raw,
            commandByte = "0x0E",
            commandByte2 = $"0x{commandByte2:X2}",
            commandByte2Bits = new
            {
                bit0FamilyCorrelatedFastRate = (commandByte2 & 0x01) != 0,
                bit1FamilyCorrelatedDownsampling = (commandByte2 & 0x02) != 0,
                bit2Dso1102Specific = (commandByte2 & 0x04) != 0,
                bit3FromConfigWord6Bit0 = (commandByte2 & 0x08) != 0
            },
            primaryTimingWord = $"0x{p.primary:X4}",
            primaryEncodedDivider = primaryDivider,
            primaryDividerFormula = p.primary == 0 ? null : "0x10001 - primaryTimingWord",
            secondaryTimingWord = $"0x{p.secondary:X4}",
            secondaryMechanicalComplementValue = secondaryMechanicalDivider,
            secondaryMeaning = "DSO-1102/vendor-DLL-specific. No matching second timing word exists in OpenHantek's public 8-byte DSO-2250 0x0E structure.",
            familyCorrelatedHardwareRateCandidateHz = familyCorrelatedRateCandidateHz,
            familyCorrelatedHardwareRateCandidateMeaning = familyCorrelatedRateCandidateHz.HasValue
                ? "Candidate only: assumes the DSO-2250 normal-mode 100 MHz base clock applies to this DSO-1102 profile. Not yet a runtime-measured DSO-1102 rate."
                : null,
            note = "Keep this hardware-programming domain separate from the CAL-referenced effective rate measured in the transferred/deinterleaved sample stream."
        };
    }

    private static object SummarizeFullBuffer(ushort[] buffer)
    {
        var lastNonZeroIndex = Array.FindLastIndex(buffer, x => x != 0);
        var populatedPrefixEstimate = lastNonZeroIndex >= 0 ? lastNonZeroIndex + 1 : 0;
        var analyzed = populatedPrefixEstimate > 0
            ? buffer.Take(populatedPrefixEstimate).ToArray()
            : Array.Empty<ushort>();

        var adc = analyzed.Where(x => x <= 0x00FF).ToArray();
        var excluded = analyzed.Length - adc.Length;
        var adcFraction = analyzed.Length > 0
            ? adc.Length / (double)analyzed.Length
            : 0.0;

        object? timing = null;
        object? plateaus = null;

        if (adc.Length >= 16)
        {
            plateaus = AnalyzeTwoPlateaus(adc);
            timing = AnalyzeSquareWaveTiming(adc);
        }

        var tailStart = Math.Max(0, analyzed.Length - 64);

        return new
        {
            allocatedCount = buffer.Length,
            populatedPrefixEstimate,
            adc8ValidCount = adc.Length,
            excludedCount = excluded,
            adc8Fraction = adcFraction,
            first16 = analyzed.Take(16).ToArray(),
            last64 = analyzed.Skip(tailStart).Take(64).ToArray(),
            twoPlateauAnalysis = plateaus,
            squareWaveTiming = timing
        };
    }

    private static object SummarizeBufferWriteExtent(ushort[] buffer, int chunkSize)
    {
        var lastNonZeroIndex = -1;
        long nonZeroCount = 0;

        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == 0)
                continue;

            nonZeroCount++;
            lastNonZeroIndex = i;
        }

        var chunkNonZeroCounts = buffer
            .Select((value, index) => new { value, index })
            .GroupBy(x => x.index / chunkSize)
            .Select(g => new
            {
                chunk = g.Key,
                startIndex = g.Key * chunkSize,
                endIndexExclusive = Math.Min(buffer.Length, (g.Key + 1) * chunkSize),
                nonZeroCount = g.Count(x => x.value != 0)
            })
            .Where(x => x.nonZeroCount > 0)
            .Take(64)
            .ToArray();

        return new
        {
            lastNonZeroIndex,
            populatedPrefixEstimate = lastNonZeroIndex >= 0 ? lastNonZeroIndex + 1 : 0,
            totalNonZeroValues = nonZeroCount,
            chunkSize,
            nonZeroChunks = chunkNonZeroCounts
        };
    }

    private static object AnalyzeTwoPlateaus(ushort[] adcSamples)
    {
        if (adcSamples.Length < 2)
            return new { detected = false };

        double low = adcSamples.Min();
        double high = adcSamples.Max();

        if (Math.Abs(high - low) < 0.5)
            return new { detected = false };

        int[] lowCluster = [];
        int[] highCluster = [];

        for (var iteration = 0; iteration < 16; iteration++)
        {
            var lows = new List<int>();
            var highs = new List<int>();

            foreach (var sample in adcSamples)
            {
                if (Math.Abs(sample - low) <= Math.Abs(sample - high))
                    lows.Add(sample);
                else
                    highs.Add(sample);
            }

            if (lows.Count == 0 || highs.Count == 0)
                return new { detected = false };

            var newLow = lows.Average();
            var newHigh = highs.Average();

            lowCluster = lows.ToArray();
            highCluster = highs.ToArray();

            if (Math.Abs(newLow - low) < 0.0001 && Math.Abs(newHigh - high) < 0.0001)
                break;

            low = newLow;
            high = newHigh;
        }

        static object SummarizeCluster(int[] values)
        {
            var mean = values.Average();
            var variance = values.Select(x =>
            {
                var d = x - mean;
                return d * d;
            }).Average();

            return new
            {
                count = values.Length,
                min = values.Min(),
                max = values.Max(),
                mean,
                standardDeviationCounts = Math.Sqrt(variance)
            };
        }

        var lowMean = lowCluster.Average();
        var highMean = highCluster.Average();
        var dutyCycleHigh = (double)highCluster.Length / adcSamples.Length;
        var minorClusterFraction = Math.Min(dutyCycleHigh, 1.0 - dutyCycleHigh);

        // Reject tiny secondary clusters caused by glitches/outliers. A real
        // square-wave plateau must occupy a meaningful part of the record.
        var detected = minorClusterFraction >= 0.05;

        return new
        {
            detected,
            confidence = detected ? "high" : "rejected_as_outlier_cluster",
            low = SummarizeCluster(lowCluster),
            high = SummarizeCluster(highCluster),
            separationCounts = highMean - lowMean,
            dutyCycleHigh,
            minorClusterFraction
        };
    }

    private static object AnalyzeSquareWaveTiming(ushort[] adcSamples)
    {
        if (adcSamples.Length < 16)
            return new { detected = false, reason = "too_few_samples" };

        double low = adcSamples.Min();
        double high = adcSamples.Max();

        if (high - low < 2.0)
            return new { detected = false, reason = "insufficient_level_separation" };

        int[] lowCluster = [];
        int[] highCluster = [];

        for (var iteration = 0; iteration < 16; iteration++)
        {
            var lows = new List<int>();
            var highs = new List<int>();

            foreach (var sample in adcSamples)
            {
                if (Math.Abs(sample - low) <= Math.Abs(sample - high))
                    lows.Add(sample);
                else
                    highs.Add(sample);
            }

            if (lows.Count == 0 || highs.Count == 0)
                return new { detected = false, reason = "empty_cluster" };

            var newLow = lows.Average();
            var newHigh = highs.Average();
            lowCluster = lows.ToArray();
            highCluster = highs.ToArray();

            if (Math.Abs(newLow - low) < 0.0001 && Math.Abs(newHigh - high) < 0.0001)
            {
                low = newLow;
                high = newHigh;
                break;
            }

            low = newLow;
            high = newHigh;
        }

        var highFraction = (double)highCluster.Length / adcSamples.Length;
        var minorFraction = Math.Min(highFraction, 1.0 - highFraction);

        if (minorFraction < 0.05)
            return new
            {
                detected = false,
                reason = "secondary_cluster_too_small",
                lowMean = low,
                highMean = high,
                highFraction,
                minorFraction
            };

        var threshold = (low + high) / 2.0;
        const int stableSamples = 4;

        var rising = new List<int>();
        var falling = new List<int>();

        var stateHigh = adcSamples[0] >= threshold;

        for (var i = 1; i <= adcSamples.Length - stableSamples; i++)
        {
            var candidateHigh = adcSamples[i] >= threshold;
            if (candidateHigh == stateHigh)
                continue;

            var stable = true;
            for (var j = 1; j < stableSamples; j++)
            {
                if ((adcSamples[i + j] >= threshold) != candidateHigh)
                {
                    stable = false;
                    break;
                }
            }

            if (!stable)
                continue;

            if (candidateHigh)
                rising.Add(i);
            else
                falling.Add(i);

            stateHigh = candidateHigh;
            i += stableSamples - 1;
        }

        static double[] Periods(IReadOnlyList<int> edges)
        {
            if (edges.Count < 2)
                return [];

            var values = new double[edges.Count - 1];
            for (var i = 1; i < edges.Count; i++)
                values[i - 1] = edges[i] - edges[i - 1];

            return values;
        }

        static object? PeriodStats(double[] values)
        {
            if (values.Length == 0)
                return null;

            var ordered = values.OrderBy(x => x).ToArray();
            var mean = values.Average();
            var variance = values.Select(x =>
            {
                var d = x - mean;
                return d * d;
            }).Average();

            double median = ordered.Length % 2 == 1
                ? ordered[ordered.Length / 2]
                : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2.0;

            return new
            {
                count = values.Length,
                min = values.Min(),
                max = values.Max(),
                mean,
                median,
                standardDeviationSamples = Math.Sqrt(variance)
            };
        }

        var risingPeriods = Periods(rising);
        var fallingPeriods = Periods(falling);
        var allPeriods = risingPeriods.Concat(fallingPeriods).ToArray();

        double? meanPeriodSamples = allPeriods.Length > 0 ? allPeriods.Average() : null;
        double? periodStdDevSamples = null;
        double? periodCv = null;

        if (allPeriods.Length > 0 && meanPeriodSamples > 0)
        {
            var mean = meanPeriodSamples.Value;
            var variance = allPeriods.Select(x =>
            {
                var d = x - mean;
                return d * d;
            }).Average();

            periodStdDevSamples = Math.Sqrt(variance);
            periodCv = periodStdDevSamples.Value / mean;
        }

        // Short high-sample-rate records can contain only about two complete
        // cycles of the 1 kHz CAL waveform. Accept one period measured from
        // each edge polarity when both agree and jitter is low. The plateau
        // population check above still rejects quiet-channel outliers.
        var hasBothPolarities =
            risingPeriods.Length >= 1 &&
            fallingPeriods.Length >= 1;

        var edgePeriodAgreement =
            hasBothPolarities &&
            Math.Abs(risingPeriods.Average() - fallingPeriods.Average()) /
                Math.Max(1.0, meanPeriodSamples ?? 1.0) <= 0.02;

        var periodic =
            allPeriods.Length >= 2 &&
            hasBothPolarities &&
            edgePeriodAgreement &&
            periodCv.HasValue &&
            periodCv.Value <= 0.05;

        double? estimatedSampleRateAt1kHz =
            periodic && meanPeriodSamples.HasValue
                ? meanPeriodSamples.Value * 1000.0
                : null;
        double? estimatedSampleIntervalNs =
            estimatedSampleRateAt1kHz.HasValue && estimatedSampleRateAt1kHz.Value > 0
                ? 1_000_000_000.0 / estimatedSampleRateAt1kHz.Value
                : null;
        double? estimatedRecordDurationMs =
            estimatedSampleRateAt1kHz.HasValue && estimatedSampleRateAt1kHz.Value > 0
                ? adcSamples.Length / estimatedSampleRateAt1kHz.Value * 1000.0
                : null;

        return new
        {
            detected = periodic,
            confidence = periodic ? "high" : "rejected_nonperiodic",
            thresholdCounts = threshold,
            stableSamplesRequired = stableSamples,
            risingEdgeCount = rising.Count,
            fallingEdgeCount = falling.Count,
            risingEdgesFirst16 = rising.Take(16).ToArray(),
            fallingEdgesFirst16 = falling.Take(16).ToArray(),
            risingPeriodSamples = PeriodStats(risingPeriods),
            fallingPeriodSamples = PeriodStats(fallingPeriods),
            combinedMeanPeriodSamples = meanPeriodSamples,
            combinedPeriodStdDevSamples = periodStdDevSamples,
            combinedPeriodCoefficientOfVariation = periodCv,
            edgePeriodAgreement,
            ifSignalIs1kHz = new
            {
                referenceFrequencyHz = 1000,
                rateDomain = "effective sample spacing in the deinterleaved acquisition stream returned by the device/DLL; the physical ADC core clock may be higher if hardware decimation is active",
                estimatedSampleRateHz = estimatedSampleRateAt1kHz,
                estimatedSampleIntervalNs,
                estimatedRecordDurationMs
            }
        };
    }

    private static object SummarizeSamples(ushort[] samples)
    {
        if (samples.Length == 0)
            return new { count = 0 };

        var rawMin = samples.Min();
        var rawMax = samples.Max();

        // Normal decoded DSO-1102 samples are byte-range ADC codes expanded to
        // 16-bit words. Values above 0x00FF are retained separately because the
        // vendor decoder can emit signed/boundary values such as 0xFFFC (-4)
        // at the capture edge.
        var adcSamples = samples.Where(x => x <= 0x00FF).ToArray();
        var nonAdcSamples = samples.Where(x => x > 0x00FF).ToArray();

        double? adcMean = null;
        double? adcStdDev = null;
        ushort? adcMin = null;
        ushort? adcMax = null;

        if (adcSamples.Length > 0)
        {
            adcMin = adcSamples.Min();
            adcMax = adcSamples.Max();
            adcMean = adcSamples.Average(x => (double)x);
            var mean = adcMean.Value;
            var variance = adcSamples
                .Select(x =>
                {
                    var d = x - mean;
                    return d * d;
                })
                .Average();
            adcStdDev = Math.Sqrt(variance);
        }

        var histogram = adcSamples
            .GroupBy(x => x)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Take(16)
            .Select(g => new { value = g.Key, count = g.Count() })
            .ToArray();

        var sorted = adcSamples.OrderBy(x => x).ToArray();

        ushort? Quantile(double q)
        {
            if (sorted.Length == 0)
                return null;

            var index = (int)Math.Round((sorted.Length - 1) * q);
            return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
        }

        return new
        {
            count = samples.Length,
            raw16 = new
            {
                min = rawMin,
                max = rawMax,
                distinctValues = samples.Distinct().Count()
            },
            adc8 = new
            {
                validCount = adcSamples.Length,
                excludedCount = nonAdcSamples.Length,
                min = adcMin,
                max = adcMax,
                peakToPeakCounts = adcMin.HasValue && adcMax.HasValue
                    ? adcMax.Value - adcMin.Value
                    : (int?)null,
                mean = adcMean,
                standardDeviationCounts = adcStdDev,
                distinctValues = adcSamples.Distinct().Count(),
                q05 = Quantile(0.05),
                median = Quantile(0.50),
                q95 = Quantile(0.95),
                histogramTop16 = histogram,
                twoPlateauAnalysis = AnalyzeTwoPlateaus(adcSamples),
                squareWaveTiming = AnalyzeSquareWaveTiming(adcSamples)
            },
            excluded16BitValues = nonAdcSamples
                .Take(16)
                .Select(x => new
                {
                    raw = x,
                    hex = $"0x{x:X4}",
                    signed = unchecked((short)x)
                })
                .ToArray(),
            first64 = samples.Take(64).ToArray()
        };
    }

    private static string CaptureStateName(ushort stateCode) =>
        stateCode switch
        {
            0 => "VALUE0",
            1 => "VALUE1",
            2 => "VALUE2",
            3 => "CAPTURE_READY",
            7 => "VALUE7",
            127 => "TIMEOUT",
            _ => $"UNKNOWN_{stateCode}"
        };
}
