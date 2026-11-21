using System.Runtime.InteropServices;
using System.Text.Json;
using DSO1102.Controller.Core.Models;

namespace DSO1102.Controller.Bridge;

// Read-only identity/status commands and the arm/force observation probes.
internal static partial class Program
{
    private static int InspectSdk(string dllPath)
    {
        var identity = VendorSdkIdentity.Inspect(dllPath);
        WriteJson(new { ok = true, command = "sdk-info", dllPath, identity, hardwareCallsMade = false });
        return 0;
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
}
