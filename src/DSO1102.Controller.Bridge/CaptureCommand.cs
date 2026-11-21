using System.Runtime.InteropServices;
using System.Text.Json;
using DSO1102.Controller.Core.Models;

namespace DSO1102.Controller.Bridge;

// The single capture path shared by all capture/self-init/frame commands.
internal static partial class Program
{
    /// <summary>Transient analog front-end state for the guarded 1 ms/div analog frame.</summary>
    private sealed record AnalogRequest(ushort Channel1Range, ushort Channel2Range, ushort Channel1Coupling, ushort Channel2Coupling)
    {
        public static readonly AnalogRequest Default1VoltDc = new(6, 6, 0, 0);

        // Only codes observed in the vendor runtime traces are accepted:
        // range 5 = 500 mV/div, 6 = 1 V/div, 7 = 2 V/div; coupling 0 = DC, 1 = AC.
        // GND is a software display mode in the vendor application and uses DC here.
        public static bool TryParse(string arguments, out AnalogRequest? request, out string? error)
        {
            request = null;
            var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4 || !parts.All(p => ushort.TryParse(p, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)))
            {
                error = "Expected four numeric arguments: ch1Range ch2Range ch1Coupling ch2Coupling.";
                return false;
            }

            var values = parts.Select(p => ushort.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (!AnalogFrontEnd.IsTracedRangeCode(values[0]) || !AnalogFrontEnd.IsTracedRangeCode(values[1]))
            {
                error = "Only runtime-traced range codes 5 (500 mV/div), 6 (1 V/div) and 7 (2 V/div) are allowed.";
                return false;
            }

            if (!AnalogFrontEnd.IsTracedCouplingCode(values[2]) || !AnalogFrontEnd.IsTracedCouplingCode(values[3]))
            {
                error = "Only coupling codes 0 (DC) and 1 (AC) are allowed.";
                return false;
            }

            request = new(values[0], values[1], values[2], values[3]);
            error = null;
            return true;
        }

        public static string RangeLabel(ushort code) => AnalogFrontEnd.RangeLabel(code);

        public static string CouplingLabel(ushort code) => AnalogFrontEnd.CouplingLabel(code);
    }

    private const string AnalogFramePrefix = "frame-1ms-analog ";

    private static int CaptureGroundBaselineV2(
        string dllPath,
        string commandName,
        bool groundReference,
        ushort? timeBaseCode = null,
        string? timeBaseLabel = null,
        double? decodedOutputReferenceRateHz = null,
        bool selfInitializeTimeBase = false,
        bool emitAdcPayload = false,
        bool selfInitializeAnalog = false,
        AnalogRequest? analog = null)
    {
        analog ??= AnalogRequest.Default1VoltDc;
        using var library = VendorLibrary.Load(dllPath);

        var search = library.GetDelegate<DsoSearchDeviceDelegate>("dsoSearchDevice");
        var getChannelLevel = library.GetDelegate<DsoGetChannelLevelDelegate>("dsoGetChannelLevel");
        var getCalData = library.GetDelegate<DsoGetCalDataDelegate>("dsoGetCalData");
        var captureStart = library.GetDelegate<DsoCaptureStartDelegate>("dsoCaptureStart");
        var triggerEnabled = library.GetDelegate<DsoTriggerEnabledDelegate>("dsoTriggerEnabled");
        var force = library.GetDelegate<DsoForceTriggerDelegate>("dsoForceTrigger");
        var getCaptureState = library.GetDelegate<DsoGetCaptureStateDelegate>("dsoGetCaptureState");
        var getChannelData = library.GetDelegate<DsoGetChannelDataDelegate>("dsoGetChannelData");
        var setTriggerAndSampleRateNew = selfInitializeTimeBase
            ? library.GetDelegate<DsoSetTriggerAndSampleRateNewDelegate>("dsoSetTriggerAndSampleRateNew")
            : null;
        var initLevelRange = selfInitializeAnalog
            ? library.GetDelegate<InitLevelRangeDelegate>("InitLevelRange")
            : null;
        var setFiltAndVoltageData = selfInitializeAnalog
            ? library.GetDelegate<DsoSetFiltAndVoltageDataDelegate>("dsoSetFiltAndVoltageData")
            : null;
        var setFilt = selfInitializeAnalog
            ? library.GetDelegate<DsoSetFiltDelegate>("dsoSetFilt")
            : null;
        var setVoltageAndCoupling = selfInitializeAnalog
            ? library.GetDelegate<DsoSetVoltageAndCouplingDelegate>("dsoSetVoltageAndCoupling")
            : null;
        var setOffset = selfInitializeAnalog
            ? library.GetDelegate<DsoSetOffsetDelegate>("dsoSetOffset")
            : null;

        var deviceIndex = Enumerable.Range(0, 4).FirstOrDefault(index => search(index) != 0, -1);
        if (deviceIndex < 0)
            return Fail("No DSO-1102 device was found at indices 0..3.");

        const ushort channelLevelCount = 0x58;
        var channelLevels = new ushort[channelLevelCount];
        var channelLevelsHandle = GCHandle.Alloc(channelLevels, GCHandleType.Pinned);

        ushort channelLevelStatus;
        try
        {
            channelLevelStatus = getChannelLevel(
                deviceIndex,
                channelLevelsHandle.AddrOfPinnedObject(),
                channelLevelCount);
        }
        finally
        {
            channelLevelsHandle.Free();
        }

        if (channelLevelStatus == 0)
            return Fail("dsoGetChannelLevel failed; refusing waveform decode without the live calibration table.");

        var calibrationA = (ushort)0;
        var calibrationB = (ushort)0;
        var calDataStatus = getCalData(deviceIndex, out calibrationA, out calibrationB);
        if (calDataStatus == 0)
            return Fail("dsoGetCalData failed; refusing waveform decode without the live calibration bytes.");

        int? timeBaseSetterResult = null;
        ushort[]? setterPrefix = null;
        object? analogInitialization = null;

        if (selfInitializeAnalog)
        {
            if (initLevelRange is null ||
                setFiltAndVoltageData is null ||
                setFilt is null ||
                setVoltageAndCoupling is null ||
                setOffset is null)
            {
                return Fail("Analog self-initialization requested, but one or more verified vendor exports could not be resolved.");
            }

            var range1 = analog.Channel1Range;
            var range2 = analog.Channel2Range;
            var coupling1 = analog.Channel1Coupling;
            var coupling2 = analog.Channel2Coupling;
            const ushort triggerSourceCh1 = 0;
            const ushort centeredPosition = 128;

            var packedCalibrationWords = Enumerable.Range(0, channelLevels.Length / 2)
                .Select(i => (ushort)((channelLevels[i * 2] << 8) | channelLevels[i * 2 + 1]))
                .ToArray();

            if (packedCalibrationWords.Length != 44)
                return Fail($"Expected 44 packed calibration words, got {packedCalibrationWords.Length}.");

            ushort[] positionState =
            [
                centeredPosition, // CH1 vertical position
                centeredPosition, // CH2 vertical position
                centeredPosition, // CH1 trigger level
                centeredPosition, // CH2 trigger level
                centeredPosition  // ALT/EXT trigger level
            ];

            var positionHandle = GCHandle.Alloc(positionState, GCHandleType.Pinned);
            var calibrationHandle = GCHandle.Alloc(packedCalibrationWords, GCHandleType.Pinned);

            int initLevelRangeResult;
            int filterVoltageResult;
            int filterResult;
            int voltageCouplingResult;
            int offsetResult;

            try
            {
                initLevelRangeResult = initLevelRange(deviceIndex);
                filterVoltageResult = setFiltAndVoltageData(
                    deviceIndex, 0, 0, range1, range2);
                filterResult = setFilt(deviceIndex, 0, 0, 0);
                voltageCouplingResult = setVoltageAndCoupling(
                    deviceIndex,
                    range1,
                    range2,
                    coupling1,
                    coupling2,
                    triggerSourceCh1);
                offsetResult = setOffset(
                    deviceIndex,
                    positionHandle.AddrOfPinnedObject(),
                    range1,
                    range2,
                    triggerSourceCh1,
                    calibrationHandle.AddrOfPinnedObject());
            }
            finally
            {
                calibrationHandle.Free();
                positionHandle.Free();
            }

            analogInitialization = new
            {
                transientOnly = true,
                channel1RangeCode = range1,
                channel2RangeCode = range2,
                channel1RangeLabel = AnalogRequest.RangeLabel(range1),
                channel2RangeLabel = AnalogRequest.RangeLabel(range2),
                channel1CouplingCode = coupling1,
                channel2CouplingCode = coupling2,
                channel1CouplingLabel = AnalogRequest.CouplingLabel(coupling1),
                channel2CouplingLabel = AnalogRequest.CouplingLabel(coupling2),
                channel1Filter = 0,
                channel2Filter = 0,
                triggerHfRejection = 0,
                triggerSourceCode = triggerSourceCh1,
                triggerSource = "CH1",
                channel1PositionRaw = centeredPosition,
                channel2PositionRaw = centeredPosition,
                triggerPositionRaw = centeredPosition,
                centeredPositionEvidence = "Strong structural/family inference (0..255 midpoint); this command exists specifically to runtime-verify it on the DSO-1102.",
                initLevelRangeResult,
                filterVoltageResult,
                filterResult,
                voltageCouplingResult,
                offsetResult
            };

            if (filterVoltageResult == 0 ||
                filterResult == 0 ||
                voltageCouplingResult == 0 ||
                offsetResult == 0)
            {
                return Fail("At least one transient analog self-initialization vendor call returned failure.");
            }
        }

        captureStart(deviceIndex);
        Thread.Sleep(3);

        if (selfInitializeTimeBase)
        {
            if (!timeBaseCode.HasValue || setTriggerAndSampleRateNew is null)
                return Fail("Self-initialization requires a verified time-base code and the New setter export.");

            // Exact prefix observed at the vendor application's real
            // dsoSetTriggerAndSampleRateNew calls for codes 15..18.
            // The only changing word in the verified profiles is word[2].
            setterPrefix =
            [
                0, 0, timeBaseCode.Value, 50, 5, 0,
                127, 192, 124, 192, 128, 0, 0, 256,
                0, 0, 0, 0, 0, 0, 0, 0, 16368
            ];

            var setterState = new ushort[256];
            Array.Copy(setterPrefix, setterState, setterPrefix.Length);
            Array.Copy(channelLevels, 0, setterState, 23, channelLevels.Length);

            var setterHandle = GCHandle.Alloc(setterState, GCHandleType.Pinned);
            try
            {
                timeBaseSetterResult = setTriggerAndSampleRateNew(
                    checked((uint)deviceIndex),
                    0,
                    setterHandle.AddrOfPinnedObject(),
                    0,
                    0,
                    0,
                    0,
                    2);
            }
            finally
            {
                setterHandle.Free();
            }

            if (timeBaseSetterResult == 0)
                return Fail("dsoSetTriggerAndSampleRateNew returned failure; capture was not continued.");

            Thread.Sleep(3);
        }

        triggerEnabled(deviceIndex);
        Thread.Sleep(3);
        force(deviceIndex);
        Thread.Sleep(3);

        var stateCode = (ushort)0;
        var triggerValue = 0u;

        for (var i = 0; i < 100; i++)
        {
            stateCode = getCaptureState(deviceIndex, out triggerValue);

            if (stateCode == 3)
                break;

            if (stateCode == 127)
                return Fail("Capture timed out before DSO-1102 ready state 3.");

            Thread.Sleep(3);
        }

        if (stateCode != 3)
            return Fail($"Capture did not reach DSO-1102 ready state 3. Last state: {stateCode}.");

        // arg4 points to word 0; arg5 points 12 bytes later (word 6).
        // The live 0x58 channel-level bytes start at word 23.
        //
        // Codes 15..18 and the normal word[4] = 6 state were observed directly
        // in dsoGetChannelData runtime traces while the vendor UI was stepped
        // through 400 us/div, 1 ms/div, 2 ms/div and 4 ms/div.
        var vendorState = new ushort[256];
        ushort[] tracedPrefix = timeBaseCode.HasValue
            ?
            [
                0, 0, timeBaseCode.Value, 50, 6, 0,
                127, 192, 124, 192, 128, 0, 0, 256,
                0, 0, 0, 0, 0, 0, 0, 0, 16368
            ]
            :
            [
                // Earlier single-profile trace retained for backward-compatible
                // capture-raw / capture-gnd-v2 diagnostics.
                0, 2, 12, 50, 0, 0,
                64, 192, 64, 192, 128, 0, 0, 256,
                0, 0, 0, 0, 0, 0, 0, 0, 16368
            ];

        Array.Copy(tracedPrefix, vendorState, tracedPrefix.Length);
        Array.Copy(channelLevels, 0, vendorState, 23, channelLevels.Length);

        const int verifiedSampleCount = 0x2800;
        const int guardBufferSamples = 1_048_576;

        var bufferA = new ushort[guardBufferSamples];
        var bufferB = new ushort[guardBufferSamples];

        var stateHandle = GCHandle.Alloc(vendorState, GCHandleType.Pinned);
        var bufferAHandle = GCHandle.Alloc(bufferA, GCHandleType.Pinned);
        var bufferBHandle = GCHandle.Alloc(bufferB, GCHandleType.Pinned);

        ushort readResult;

        try
        {
            var stateBase = stateHandle.AddrOfPinnedObject();

            readResult = getChannelData(
                checked((ushort)deviceIndex),
                bufferAHandle.AddrOfPinnedObject(),
                bufferBHandle.AddrOfPinnedObject(),
                stateBase,
                IntPtr.Add(stateBase, 12),
                triggerValue,
                calibrationA,
                calibrationB);
        }
        finally
        {
            bufferBHandle.Free();
            bufferAHandle.Free();
            stateHandle.Free();
        }

        var a = bufferA.Take(verifiedSampleCount).ToArray();
        var b = bufferB.Take(verifiedSampleCount).ToArray();

        // The vendor DLL has mode-dependent larger-memory paths. Do not assume
        // the historical 0x2800 analysis window is the complete acquisition.
        // Buffers are zero-initialized, while real decoded CH1/CH2 data for the
        // current test setup is non-zero. Scanning the whole guard allocation
        // therefore gives a conservative estimate of how far the vendor call
        // actually populated each output buffer.
        // Live frame commands only need the payload. The full-buffer diagnostics scan
        // and group all 1,048,576 guard samples per channel and dominated the frame
        // latency in Run mode, so they are reserved for the diagnostic commands.
        // This is host-side computation only; the vendor call sequence is unchanged.
        var leanFrame = emitAdcPayload;
        object? writeExtentA = leanFrame ? null : SummarizeBufferWriteExtent(bufferA, verifiedSampleCount);
        object? writeExtentB = leanFrame ? null : SummarizeBufferWriteExtent(bufferB, verifiedSampleCount);
        object? fullBufferA = leanFrame ? null : SummarizeFullBuffer(bufferA);
        object? fullBufferB = leanFrame ? null : SummarizeFullBuffer(bufferB);
        var allZeroA = a.All(x => x == 0);
        var allZeroB = b.All(x => x == 0);

        var adcRangeFractionA = a.Count(x => x <= 0x00FF) / (double)a.Length;
        var adcRangeFractionB = b.Count(x => x <= 0x00FF) / (double)b.Length;

        // A normal vendor-decoded record is byte-range ADC data expanded to
        // UInt16, with at most a very small number of boundary/sentinel values.
        // Reject stale/misaligned decoder states that return thousands of
        // 0xFFxx words even when dsoGetChannelData itself returns success.
        var decoderLooksSane =
            adcRangeFractionA >= 0.95 &&
            adcRangeFractionB >= 0.95;

        var waveformReadValid =
            readResult != 0 &&
            !(allZeroA && allZeroB) &&
            decoderLooksSane;

        object? adcPayload = null;
        if (emitAdcPayload && waveformReadValid)
        {
            var payloadSampleCount = tracedPrefix[10] == 0 ? 0x2800 : 0x80000;
            var payloadA = new ushort[payloadSampleCount];
            var payloadB = new ushort[payloadSampleCount];
            Array.Copy(bufferA, payloadA, payloadSampleCount);
            Array.Copy(bufferB, payloadB, payloadSampleCount);

            var bytesA = new byte[payloadA.Length * sizeof(ushort)];
            var bytesB = new byte[payloadB.Length * sizeof(ushort)];
            Buffer.BlockCopy(payloadA, 0, bytesA, 0, bytesA.Length);
            Buffer.BlockCopy(payloadB, 0, bytesB, 0, bytesB.Length);

            adcPayload = new
            {
                encoding = "u16le-base64",
                sampleDomain = "DSO1102USB.dll decoded ADC counts before vendor-EXE post-correction",
                sampleCountPerChannel = payloadSampleCount,
                channel1 = Convert.ToBase64String(bytesA),
                channel2 = Convert.ToBase64String(bytesB)
            };
        }

        WriteJson(new
        {
            ok = waveformReadValid,
            command = commandName,
            deviceIndex,
            abi = new
            {
                verifiedFromVendorRuntimeTrace = true,
                arg1 = "deviceIndex (low 16 bits)",
                arg2 = "waveform buffer A",
                arg3 = "waveform buffer B",
                arg4 = "trigger/sample configuration pointer",
                arg5 = "offset/calibration-state pointer = arg4 + 12 bytes",
                arg6 = "trigger/capture value returned by dsoGetCaptureState",
                arg7 = "calibrationA returned by dsoGetCalData",
                arg8 = "calibrationB returned by dsoGetCalData"
            },
            profile = new
            {
                name = timeBaseCode.HasValue
                    ? $"vendor-traced {timeBaseLabel} read profile"
                    : "vendor-traced CH1 profile",
                requiresOriginalApplicationInitialization = !selfInitializeTimeBase,
                selfInitializedTimeBase = selfInitializeTimeBase,
                timeBaseSetterResult,
                setterWords = setterPrefix?.Take(23).ToArray(),
                selfInitializedAnalog = selfInitializeAnalog,
                analogInitialization,
                timeBaseCode,
                timeBaseLabel,
                decodedOutputReferenceRateHz,
                calReferencedEffectiveAcquisitionRateHz = decodedOutputReferenceRateHz,
                decodedOutputRateMeaning = "Rate inferred from sample spacing in the deinterleaved hardware-transfer stream. Static dsoGetChannelData analysis shows no time-axis interpolation for the characterized Time/DIV>=10 deep-memory path; the ADC core clock may still be higher if hardware decimation is active.",
                hardwareSamplerateProgramming = DescribeHardwareSamplerateProgramming(timeBaseCode, tracedPrefix[4], tracedPrefix[6]),
                decodedRecordDepthProgramming = new
                {
                    stateWord10 = tracedPrefix[10],
                    samplesPerChannel = tracedPrefix[10] == 0 ? 0x2800 : 0x80000,
                    rawTransferBytes = tracedPrefix[10] == 0 ? 0x5000 : 0x100000,
                    interpretation = tracedPrefix[10] == 0
                        ? "small decoded record: 10,240 samples/channel"
                        : "deep decoded record: 524,288 samples/channel",
                    source = "Direct static disassembly of DSO1102USB.dll dsoGetChannelData."
                },
                triggerSampleWords = tracedPrefix.Take(6).ToArray(),
                triggerSampleState = new
                {
                    triggerSourceCode = tracedPrefix[0],
                    triggerSource = TriggerSourceName(tracedPrefix[0]),
                    fastTimebaseHardwareChannelModeRaw = tracedPrefix[1],
                    timeBaseCode = tracedPrefix[2],
                    triggerPositionPercent = tracedPrefix[3],
                    recordLengthStateRaw = tracedPrefix[4],
                    hardwareRecordLengthMode = tracedPrefix[4] == 0 ? 1 : 2,
                    word5Unknown = tracedPrefix[5],
                    note = "word[3] and word[4] roles verified by direct DSO1102USB.dll disassembly; record-length helper distinguishes only zero vs non-zero."
                },
                calibrationA,
                calibrationB,
                channelLevelCount = channelLevels.Length
            },
            capture = new
            {
                stateCode,
                stateName = CaptureStateName(stateCode),
                triggerValue,
                vendorReadResult = readResult,
                analysisWindowSamplesPerBuffer = verifiedSampleCount,
                analysisWindowSource = "0x2800 branch previously verified in the original application.",
                guardBufferSamplesPerChannel = guardBufferSamples,
                bufferWriteExtentA = writeExtentA,
                bufferWriteExtentB = writeExtentB,
                fullBufferA,
                fullBufferB,
                adcPayload
            },
            bufferA = leanFrame ? null : SummarizeSamples(a),
            bufferB = leanFrame ? null : SummarizeSamples(b),
            diagnosticsOmitted = leanFrame,
            interpretation = new
            {
                waveformReadValid,
                allZeroA,
                allZeroB,
                channelMapping = waveformReadValid
                    ? groundReference
                        ? "Buffer A is CH1 and Buffer B is CH2. Mapping was confirmed by a later driven-signal test on CH1."
                        : "Buffer A = CH1, Buffer B = CH2 (confirmed by driven-signal comparison against the grounded baseline)."
                    : "Not evaluated because the vendor call did not populate either buffer.",
                physicalVoltageConversionApplied = false,
                vendorCalibrationStateUsed = true,
                vendorApplicationPostCorrectionApplied = false,
                sampleDomain = "DSO1102USB.dll decoded ADC counts (nominal 0..255, UInt16 container)",
                postCorrectionNote = "The original vendor EXE can apply an additional per-channel software gain correction around the channel reference level after dsoGetChannelData. Bridge captures intentionally expose the DLL output before that EXE-only stage."
            },
            safety = new
            {
                persistentConfigurationChanged = false,
                transientTimeBaseConfigurationChanged = selfInitializeTimeBase && timeBaseSetterResult != 0,
                transientAnalogConfigurationChanged = selfInitializeAnalog,
                calibrationWritten = false,
                flashWritten = false,
                deviceIdWritten = false,
                configurationSettersCalled = selfInitializeTimeBase || selfInitializeAnalog,
                configurationSetter = selfInitializeAnalog
                    ? "InitLevelRange + dsoSetFiltAndVoltageData + dsoSetFilt + dsoSetVoltageAndCoupling + dsoSetOffset + dsoSetTriggerAndSampleRateNew"
                    : selfInitializeTimeBase ? "dsoSetTriggerAndSampleRateNew" : null,
                waveformRead = waveformReadValid
            }
        });

        return waveformReadValid ? 0 : 2;
    }
}
