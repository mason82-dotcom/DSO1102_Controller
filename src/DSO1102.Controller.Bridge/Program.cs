using System.Runtime.InteropServices;
using System.Text.Json;

namespace DSO1102.Controller.Bridge;

internal static class Program
{
    private const uint LoadWithAlteredSearchPath = 0x00000008;
    private static bool _compactJson;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate ushort DsoSearchDeviceDelegate(int deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DsoGetFpgaVersionDelegate(int deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DsoGetDeviceIdDelegate(int deviceIndex, out ushort deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate ushort DsoGetDeviceAddressDelegate(int deviceIndex, out ushort deviceAddress);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate ushort DsoGetChannelLevelDelegate(int deviceIndex, IntPtr values, ushort count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate ushort DsoGetCalDataDelegate(int deviceIndex, out ushort calibrationA, out ushort calibrationB);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate ushort DsoGetCaptureStateDelegate(int deviceIndex, out uint captureValue);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate ushort DsoCaptureStartDelegate(int deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DsoTriggerEnabledDelegate(int deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DsoForceTriggerDelegate(int deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate ushort DsoGetChannelDataDelegate(
        ushort deviceIndex,
        IntPtr bufferA,
        IntPtr bufferB,
        IntPtr triggerSampleConfig,
        IntPtr offsetCalibrationState,
        uint triggerValue,
        ushort calibrationA,
        ushort calibrationB);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DsoSetTriggerAndSampleRateNewDelegate(
        uint deviceIndex,
        uint reserved2,
        IntPtr triggerSampleConfig,
        uint reserved4,
        uint reserved5,
        uint reserved6,
        uint reserved7,
        uint mode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitLevelRangeDelegate(int deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DsoSetFiltAndVoltageDataDelegate(
        int deviceIndex,
        ushort channel1Filter,
        ushort channel2Filter,
        ushort channel1Range,
        ushort channel2Range);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DsoSetFiltDelegate(
        int deviceIndex,
        ushort channel1Filter,
        ushort channel2Filter,
        ushort triggerHfRejection);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DsoSetVoltageAndCouplingDelegate(
        int deviceIndex,
        ushort channel1Range,
        ushort channel2Range,
        ushort channel1Coupling,
        ushort channel2Coupling,
        ushort triggerSource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DsoSetOffsetDelegate(
        int deviceIndex,
        IntPtr positionState,
        ushort channel1Range,
        ushort channel2Range,
        ushort triggerSource,
        IntPtr packedCalibrationWords);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    private static int Main(string[] args)
    {
        try
        {
            var command = args.Length > 0 ? args[0].ToLowerInvariant() : "probe";
            var dllPath = ResolveDllPath(args.Skip(1).ToArray());

            if (command == "server")
                return RunServer(dllPath);

            return ExecuteCommand(command, dllPath);
        }
        catch (Exception ex)
        {
            return Fail(ex.ToString());
        }
    }

    private static int ExecuteCommand(string command, string dllPath) =>
        command switch
        {
            "probe" => Probe(dllPath),
            "info" => ReadDeviceInfo(dllPath),
            "arm" => ArmAndObserve(dllPath, forceTrigger: false),
            "force" => ArmAndObserve(dllPath, forceTrigger: true),
            "capture-gnd" => Fail("capture-gnd v1 is disabled because it used an incorrect vendor ABI. Use capture-gnd-v2 after initializing the known profile in the original application."),
            "capture-gnd-v2" => CaptureGroundBaselineV2(dllPath, "capture-gnd-v2", groundReference: true),
            "capture-raw" => CaptureGroundBaselineV2(dllPath, "capture-raw", groundReference: false),
            "capture-400us" => CaptureGroundBaselineV2(dllPath, "capture-400us", groundReference: false, timeBaseCode: 15, timeBaseLabel: "400 us/div", decodedOutputReferenceRateHz: 5_000_000),
            "capture-1ms" => CaptureGroundBaselineV2(dllPath, "capture-1ms", groundReference: false, timeBaseCode: 16, timeBaseLabel: "1 ms/div", decodedOutputReferenceRateHz: 5_000_000),
            "capture-2ms" => CaptureGroundBaselineV2(dllPath, "capture-2ms", groundReference: false, timeBaseCode: 17, timeBaseLabel: "2 ms/div", decodedOutputReferenceRateHz: 5_000_000),
            "capture-4ms" => CaptureGroundBaselineV2(dllPath, "capture-4ms", groundReference: false, timeBaseCode: 18, timeBaseLabel: "4 ms/div", decodedOutputReferenceRateHz: 5_000_000),
            "self-init-400us" => CaptureGroundBaselineV2(dllPath, "self-init-400us", groundReference: false, timeBaseCode: 15, timeBaseLabel: "400 us/div", decodedOutputReferenceRateHz: 5_000_000, selfInitializeTimeBase: true),
            "self-init-1ms" => CaptureGroundBaselineV2(dllPath, "self-init-1ms", groundReference: false, timeBaseCode: 16, timeBaseLabel: "1 ms/div", decodedOutputReferenceRateHz: 5_000_000, selfInitializeTimeBase: true),
            "self-init-2ms" => CaptureGroundBaselineV2(dllPath, "self-init-2ms", groundReference: false, timeBaseCode: 17, timeBaseLabel: "2 ms/div", decodedOutputReferenceRateHz: 5_000_000, selfInitializeTimeBase: true),
            "self-init-4ms" => CaptureGroundBaselineV2(dllPath, "self-init-4ms", groundReference: false, timeBaseCode: 18, timeBaseLabel: "4 ms/div", decodedOutputReferenceRateHz: 5_000_000, selfInitializeTimeBase: true),
            "frame-400us-adc" => CaptureGroundBaselineV2(dllPath, "frame-400us-adc", groundReference: false, timeBaseCode: 15, timeBaseLabel: "400 us/div", decodedOutputReferenceRateHz: 5_000_000, selfInitializeTimeBase: true, emitAdcPayload: true),
            "frame-1ms-adc" => CaptureGroundBaselineV2(dllPath, "frame-1ms-adc", groundReference: false, timeBaseCode: 16, timeBaseLabel: "1 ms/div", decodedOutputReferenceRateHz: 5_000_000, selfInitializeTimeBase: true, emitAdcPayload: true),
            "self-init-1ms-analog" => CaptureGroundBaselineV2(dllPath, "self-init-1ms-analog", groundReference: false, timeBaseCode: 16, timeBaseLabel: "1 ms/div", decodedOutputReferenceRateHz: 5_000_000, selfInitializeTimeBase: true, selfInitializeAnalog: true),
            "exports" => CheckExports(dllPath),
            _ => Fail($"Unknown command '{command}'. Supported: probe, info, arm, force, capture-raw, capture-gnd-v2, capture-400us, capture-1ms, capture-2ms, capture-4ms, self-init-400us, self-init-1ms, self-init-2ms, self-init-4ms, frame-400us-adc, frame-1ms-adc, self-init-1ms-analog, exports.")
        };

    private static int RunServer(string dllPath)
    {
        _compactJson = true;

        while (true)
        {
            var line = Console.ReadLine();
            if (line is null)
                return 0;

            var command = line.Trim().ToLowerInvariant();
            if (command.Length == 0)
                continue;

            if (command is "quit" or "exit")
                return 0;

            try
            {
                ExecuteCommand(command, dllPath);
            }
            catch (Exception ex)
            {
                Fail(ex.ToString());
            }

            Console.Out.Flush();
        }
    }

    private static string ResolveDllPath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--dll", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(args[i + 1]);
        }

        var configured = Environment.GetEnvironmentVariable("DSO1102_SDK_DLL");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "DSO-1102 USB",
            "DSO1102USB.dll");
    }

    private static int Probe(string dllPath)
    {
        using var library = VendorLibrary.Load(dllPath);

        var search = library.GetDelegate<DsoSearchDeviceDelegate>("dsoSearchDevice");
        var getFpgaVersion = library.GetDelegate<DsoGetFpgaVersionDelegate>("dsoGetFPGAVersion");

        var devices = new List<object>();

        for (var index = 0; index < 4; index++)
        {
            var present = search(index) != 0;
            int? fpgaVersion = null;

            if (present)
                fpgaVersion = getFpgaVersion(index);

            devices.Add(new
            {
                index,
                present,
                fpgaVersion
            });
        }

        WriteJson(new
        {
            ok = true,
            command = "probe",
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            dllPath,
            devicePathPattern = @"\\.\D1102-%d",
            devices
        });

        return 0;
    }

    private static int ReadDeviceInfo(string dllPath)
    {
        using var library = VendorLibrary.Load(dllPath);

        var search = library.GetDelegate<DsoSearchDeviceDelegate>("dsoSearchDevice");
        var getFpgaVersion = library.GetDelegate<DsoGetFpgaVersionDelegate>("dsoGetFPGAVersion");
        var getDeviceId = library.GetDelegate<DsoGetDeviceIdDelegate>("dsoGetDeviceID");
        var getDeviceAddress = library.GetDelegate<DsoGetDeviceAddressDelegate>("dsoGetDeviceAddress");
        var getChannelLevel = library.GetDelegate<DsoGetChannelLevelDelegate>("dsoGetChannelLevel");
        var getCalData = library.GetDelegate<DsoGetCalDataDelegate>("dsoGetCalData");
        var getCaptureState = library.GetDelegate<DsoGetCaptureStateDelegate>("dsoGetCaptureState");

        var deviceIndex = Enumerable.Range(0, 4).FirstOrDefault(index => search(index) != 0, -1);
        if (deviceIndex < 0)
            return Fail("No DSO-1102 device was found at indices 0..3.");

        var fpgaVersion = getFpgaVersion(deviceIndex);

        var deviceId = (ushort)0;
        var deviceIdStatus = getDeviceId(deviceIndex, out deviceId);

        var deviceAddress = (ushort)0;
        var deviceAddressStatus = getDeviceAddress(deviceIndex, out deviceAddress);

        const ushort channelLevelCount = 0x58;
        var channelLevels = new ushort[channelLevelCount];
        var levelsHandle = GCHandle.Alloc(channelLevels, GCHandleType.Pinned);
        ushort channelLevelStatus;

        try
        {
            channelLevelStatus = getChannelLevel(
                deviceIndex,
                levelsHandle.AddrOfPinnedObject(),
                channelLevelCount);
        }
        finally
        {
            levelsHandle.Free();
        }

        var packedCalibrationWords = Enumerable.Range(0, channelLevels.Length / 2)
            .Select(i => (ushort)((channelLevels[i * 2] << 8) | channelLevels[i * 2 + 1]))
            .ToArray();

        string[] verticalRangeLabels =
        [
            "10 mV/div",
            "20 mV/div",
            "50 mV/div",
            "100 mV/div",
            "200 mV/div",
            "500 mV/div",
            "1 V/div",
            "2 V/div",
            "5 V/div"
        ];

        var channel1RangeCalibration = Enumerable.Range(0, 9)
            .Select(i => new
            {
                rangeCode = i,
                rangeLabel = verticalRangeLabels[i],
                rangeLabelEvidence = i is 5 or 6 or 7
                    ? "DSO-1102 runtime verified"
                    : "Hantek DSO-2000 family corroborated; DSO-1102 runtime verification pending",
                start = packedCalibrationWords[i * 2],
                end = packedCalibrationWords[i * 2 + 1]
            })
            .ToArray();

        var channel2RangeCalibration = Enumerable.Range(0, 9)
            .Select(i => new
            {
                rangeCode = i,
                rangeLabel = verticalRangeLabels[i],
                rangeLabelEvidence = i is 5 or 6 or 7
                    ? "DSO-1102 runtime verified"
                    : "Hantek DSO-2000 family corroborated; DSO-1102 runtime verification pending",
                start = packedCalibrationWords[18 + i * 2],
                end = packedCalibrationWords[18 + i * 2 + 1]
            })
            .ToArray();

        var triggerCalibration = new
        {
            selector0 = new
            {
                start = packedCalibrationWords[36],
                end = packedCalibrationWords[37]
            },
            selector1 = new
            {
                start = packedCalibrationWords[38],
                end = packedCalibrationWords[39]
            },
            currentlyUnusedByDsoSetOffset = new
            {
                start = packedCalibrationWords[40],
                end = packedCalibrationWords[41]
            },
            selectorOther = new
            {
                start = packedCalibrationWords[42],
                end = packedCalibrationWords[43]
            }
        };

        var calibrationA = (ushort)0;
        var calibrationB = (ushort)0;
        var calDataStatus = getCalData(deviceIndex, out calibrationA, out calibrationB);

        var captureValue = 0u;
        var captureStateCode = getCaptureState(deviceIndex, out captureValue);

        WriteJson(new
        {
            ok = true,
            command = "info",
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            dllPath,
            device = new
            {
                index = deviceIndex,
                fpgaVersion,
                deviceId = new
                {
                    callSucceeded = deviceIdStatus != 0,
                    raw = deviceId
                },
                deviceAddress = new
                {
                    callSucceeded = deviceAddressStatus != 0,
                    raw = deviceAddress
                },
                channelLevels = new
                {
                    callSucceeded = channelLevelStatus != 0,
                    rawByteCount = channelLevels.Length,
                    rawBytesExpandedToWords = channelLevels,
                    packedWordCount = packedCalibrationWords.Length,
                    packedWords = packedCalibrationWords,
                    decodedLayout = new
                    {
                        source = "Verified statically from DSO1102USB.dll dsoSetOffset RVA 0x3180.",
                        channel1Ranges = channel1RangeCalibration,
                        channel2Ranges = channel2RangeCalibration,
                        triggerCalibration
                    }
                },
                calData = new
                {
                    callSucceeded = calDataStatus != 0,
                    calibrationA,
                    calibrationB
                },
                captureState = new
                {
                    stateCode = captureStateCode,
                    stateName = CaptureStateName(captureStateCode),
                    rawTriggerValue = captureValue
                }
            },
            safety = new
            {
                mode = "read-only",
                writeConfigurationCallsUsed = false
            }
        });

        return 0;
    }

    private static int ArmAndObserve(string dllPath, bool forceTrigger)
    {
        using var library = VendorLibrary.Load(dllPath);

        var search = library.GetDelegate<DsoSearchDeviceDelegate>("dsoSearchDevice");
        var captureStart = library.GetDelegate<DsoCaptureStartDelegate>("dsoCaptureStart");
        var triggerEnabled = library.GetDelegate<DsoTriggerEnabledDelegate>("dsoTriggerEnabled");
        var force = library.GetDelegate<DsoForceTriggerDelegate>("dsoForceTrigger");
        var getCaptureState = library.GetDelegate<DsoGetCaptureStateDelegate>("dsoGetCaptureState");

        var deviceIndex = Enumerable.Range(0, 4).FirstOrDefault(index => search(index) != 0, -1);
        if (deviceIndex < 0)
            return Fail("No DSO-1102 device was found at indices 0..3.");

        var beforeValue = 0u;
        var beforeState = getCaptureState(deviceIndex, out beforeValue);

        var captureStartResult = captureStart(deviceIndex);
        var triggerEnabledResult = triggerEnabled(deviceIndex);

        int? forceResult = null;
        if (forceTrigger)
            forceResult = force(deviceIndex);

        var samples = new List<object>();

        for (var i = 0; i < 20; i++)
        {
            Thread.Sleep(50);

            var triggerValue = 0u;
            var stateCode = getCaptureState(deviceIndex, out triggerValue);

            samples.Add(new
            {
                elapsedMs = (i + 1) * 50,
                stateCode,
                stateName = CaptureStateName(stateCode),
                rawTriggerValue = triggerValue
            });

            if (stateCode == 3 || stateCode == 127)
                break;
        }

        WriteJson(new
        {
            ok = true,
            command = forceTrigger ? "force" : "arm",
            deviceIndex,
            before = new
            {
                stateCode = beforeState,
                stateName = CaptureStateName(beforeState),
                rawTriggerValue = beforeValue
            },
            captureStartResult,
            triggerEnabledResult,
            forceTriggerResult = forceResult,
            observations = samples,
            safety = new
            {
                persistentConfigurationChanged = false,
                calibrationWritten = false,
                flashWritten = false,
                deviceIdWritten = false,
                waveformRead = false
            }
        });

        return 0;
    }

    private static int CaptureGroundBaseline(string dllPath)
    {
        using var library = VendorLibrary.Load(dllPath);

        var search = library.GetDelegate<DsoSearchDeviceDelegate>("dsoSearchDevice");
        var captureStart = library.GetDelegate<DsoCaptureStartDelegate>("dsoCaptureStart");
        var triggerEnabled = library.GetDelegate<DsoTriggerEnabledDelegate>("dsoTriggerEnabled");
        var force = library.GetDelegate<DsoForceTriggerDelegate>("dsoForceTrigger");
        var getCaptureState = library.GetDelegate<DsoGetCaptureStateDelegate>("dsoGetCaptureState");
        var getChannelData = library.GetDelegate<DsoGetChannelDataDelegate>("dsoGetChannelData");

        var deviceIndex = Enumerable.Range(0, 4).FirstOrDefault(index => search(index) != 0, -1);
        if (deviceIndex < 0)
            return Fail("No DSO-1102 device was found at indices 0..3.");

        captureStart(deviceIndex);
        Thread.Sleep(3);
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
                return Fail("Capture timed out before a readable state was reached.");

            Thread.Sleep(3);
        }

        if (stateCode != 3)
            return Fail($"Capture did not reach DSO-1102 ready state 3. Last state: {stateCode}.");

        const int analysisSampleCount = 30_000;
        const int guardBufferSamples = 1_048_576;

        // The vendor DLL uses two six-word configuration blocks while decoding data.
        // These conservative values match the already initialized CH1 / 1 ms/div /
        // small-memory test setup closely enough for a first GND-baseline read.
        // No configuration setter is called here.
        var captureConfig = new ushort[]
        {
            1,  // trigger source: CH1 family value
            0,  // selected channel: CH1
            6,  // 1 ms/div family time-base index
            50, // nominal 50% trigger position
            0,  // small RAM
            0
        };

        var channelConfig = new ushort[6];
        var bufferA = new ushort[guardBufferSamples];
        var bufferB = new ushort[guardBufferSamples];

        var handles = new[]
        {
            GCHandle.Alloc(bufferA, GCHandleType.Pinned),
            GCHandle.Alloc(bufferB, GCHandleType.Pinned),
            GCHandle.Alloc(captureConfig, GCHandleType.Pinned),
            GCHandle.Alloc(channelConfig, GCHandleType.Pinned)
        };

        ushort readResult;

        try
        {
            readResult = getChannelData(
                checked((ushort)deviceIndex),
                handles[0].AddrOfPinnedObject(),
                handles[1].AddrOfPinnedObject(),
                handles[2].AddrOfPinnedObject(),
                handles[3].AddrOfPinnedObject(),
                triggerValue,
                0,
                0);
        }
        finally
        {
            foreach (var handle in handles)
                if (handle.IsAllocated)
                    handle.Free();
        }

        var a = bufferA.Take(analysisSampleCount).ToArray();
        var b = bufferB.Take(analysisSampleCount).ToArray();
        var allZeroA = a.All(x => x == 0);
        var allZeroB = b.All(x => x == 0);
        var waveformReadValid = readResult != 0 && !(allZeroA && allZeroB);

        WriteJson(new
        {
            ok = true,
            command = "capture-gnd",
            deviceIndex,
            capture = new
            {
                stateCode,
                stateName = CaptureStateName(stateCode),
                triggerValue,
                vendorReadResult = readResult,
                analysisSampleCountPerBuffer = analysisSampleCount,
                sampleCountVerified = false,
                configurationSource = "Original software initialized device; bridge performs no Set* call."
            },
            bufferA = SummarizeSamples(a),
            bufferB = SummarizeSamples(b),
            interpretation = new
            {
                waveformReadValid,
                allZeroA,
                allZeroB,
                channelMapping = waveformReadValid
                    ? "Unresolved by code. With CH1 physically tied to GND, the flatter/lower-noise buffer identifies CH1 empirically."
                    : "Not evaluated because both output buffers remained unchanged/all-zero.",
                voltageCalibrationApplied = false,
                diagnostic = waveformReadValid
                    ? "Vendor DLL populated at least one output buffer."
                    : "Capture reached ready state, but dsoGetChannelData did not populate either output buffer. ABI/auxiliary parameters remain unverified."
            },
            safety = new
            {
                persistentConfigurationChanged = false,
                calibrationWritten = false,
                flashWritten = false,
                deviceIdWritten = false,
                configurationSettersCalled = false,
                waveformRead = waveformReadValid
            }
        });

        return 0;
    }

    private static int CaptureGroundBaselineV2(
        string dllPath,
        string commandName,
        bool groundReference,
        ushort? timeBaseCode = null,
        string? timeBaseLabel = null,
        double? decodedOutputReferenceRateHz = null,
        bool selfInitializeTimeBase = false,
        bool emitAdcPayload = false,
        bool selfInitializeAnalog = false)
    {
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

            const ushort range1V = 6;
            const ushort couplingDc = 0;
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
                    deviceIndex, 0, 0, range1V, range1V);
                filterResult = setFilt(deviceIndex, 0, 0, 0);
                voltageCouplingResult = setVoltageAndCoupling(
                    deviceIndex,
                    range1V,
                    range1V,
                    couplingDc,
                    couplingDc,
                    triggerSourceCh1);
                offsetResult = setOffset(
                    deviceIndex,
                    positionHandle.AddrOfPinnedObject(),
                    range1V,
                    range1V,
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
                channel1RangeCode = range1V,
                channel2RangeCode = range1V,
                rangeLabel = "1 V/div",
                channel1CouplingCode = couplingDc,
                channel2CouplingCode = couplingDc,
                couplingLabel = "DC",
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
        var writeExtentA = SummarizeBufferWriteExtent(bufferA, verifiedSampleCount);
        var writeExtentB = SummarizeBufferWriteExtent(bufferB, verifiedSampleCount);
        var fullBufferA = SummarizeFullBuffer(bufferA);
        var fullBufferB = SummarizeFullBuffer(bufferB);
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
            bufferA = SummarizeSamples(a),
            bufferB = SummarizeSamples(b),
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

    private static int CheckExports(string dllPath)
    {
        using var library = VendorLibrary.Load(dllPath);

        var names = new[]
        {
            "dsoSearchDevice",
            "dsoGetFPGAVersion",
            "dsoGetDeviceID",
            "dsoGetDeviceAddress",
            "dsoGetChannelLevel",
            "dsoGetCalData",
            "dsoSetCalData",
            "dsoSetChannelLevel",
            "dsoGetCalTrigState",
            "dsoSetOffset",
            "dsoGetChannelData",
            "dsoTriggerEnabled",
            "dsoCaptureStart",
            "dsoSetTriggerAndSampleRate",
            "dsoSetTriggerAndSampleRateNew",
            "dsoGetLogicData",
            "dsoForceTrigger",
            "dsoGetCaptureState",
            "dsoSetVoltageAndCouplingFirst",
            "dsoSetVoltageAndCouplingSecond",
            "dsoSetFilt",
            "dsoSetFiltAndVoltageData",
            "dsoSetVoltageAndCoupling",
            "dsoFFT",
            "dsoFFTGetSamples",
            "dsoFFTBuffer",
            "dsoSetDeviceID",
            "InitLevelRange"
        };

        WriteJson(new
        {
            ok = true,
            command = "exports",
            dllPath,
            exports = names.Select(name => new
            {
                name,
                available = library.HasExport(name)
            })
        });

        return 0;
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

    private static void WriteJson(object value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = !_compactJson
        }));

    private static int Fail(string message)
    {
        WriteJson(new { ok = false, error = message });
        return 1;
    }

    private sealed class VendorLibrary : IDisposable
    {
        private IntPtr _module;

        private VendorLibrary(IntPtr module) => _module = module;

        public static VendorLibrary Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("DSO1102 vendor DLL was not found.", path);

            var module = LoadLibraryExW(path, IntPtr.Zero, LoadWithAlteredSearchPath);
            if (module == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"LoadLibraryExW failed for '{path}'.");

            return new VendorLibrary(module);
        }

        public bool HasExport(string name) =>
            GetProcAddress(_module, name) != IntPtr.Zero;

        public T GetDelegate<T>(string name) where T : Delegate
        {
            var proc = GetProcAddress(_module, name);
            if (proc == IntPtr.Zero)
                throw new EntryPointNotFoundException($"Export '{name}' was not found.");

            return Marshal.GetDelegateForFunctionPointer<T>(proc);
        }

        public void Dispose()
        {
            if (_module == IntPtr.Zero)
                return;

            FreeLibrary(_module);
            _module = IntPtr.Zero;
        }
    }
}
