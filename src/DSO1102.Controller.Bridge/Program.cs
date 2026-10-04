using System.Runtime.InteropServices;
using System.Text.Json;

namespace DSO1102.Controller.Bridge;

internal static class Program
{
    private const uint LoadWithAlteredSearchPath = 0x00000008;

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
    private delegate ushort DsoGetCaptureStateDelegate(int deviceIndex, out uint captureValue);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate ushort DsoCaptureStartDelegate(int deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DsoTriggerEnabledDelegate(int deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DsoForceTriggerDelegate(int deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate ushort DsoGetChannelDataDelegate(
        int deviceIndex,
        IntPtr bufferA,
        IntPtr bufferB,
        IntPtr captureConfig,
        IntPtr channelConfig,
        uint triggerValue,
        int correctionA,
        int correctionB);

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

            return command switch
            {
                "probe" => Probe(dllPath),
                "info" => ReadDeviceInfo(dllPath),
                "arm" => ArmAndObserve(dllPath, forceTrigger: false),
                "force" => ArmAndObserve(dllPath, forceTrigger: true),
                "capture-gnd" => CaptureGroundBaseline(dllPath),
                "exports" => CheckExports(dllPath),
                _ => Fail($"Unknown command '{command}'. Supported: probe, info, arm, force, capture-gnd, exports.")
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.ToString());
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
                    packedWords = packedCalibrationWords
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

        const int smallBufferSamples = 10_240;
        const int guardBufferSamples = 524_288;

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
                deviceIndex,
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

        var a = bufferA.Take(smallBufferSamples).ToArray();
        var b = bufferB.Take(smallBufferSamples).ToArray();

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
                assumedSampleCountPerBuffer = smallBufferSamples,
                configurationSource = "Original software initialized device; bridge performs no Set* call."
            },
            bufferA = SummarizeSamples(a),
            bufferB = SummarizeSamples(b),
            interpretation = new
            {
                channelMapping = "Unresolved by code. With CH1 physically tied to GND, the flatter/lower-noise buffer identifies CH1 empirically.",
                voltageCalibrationApplied = false
            },
            safety = new
            {
                persistentConfigurationChanged = false,
                calibrationWritten = false,
                flashWritten = false,
                deviceIdWritten = false,
                configurationSettersCalled = false,
                waveformRead = true
            }
        });

        return 0;
    }

    private static object SummarizeSamples(ushort[] samples)
    {
        if (samples.Length == 0)
            return new { count = 0 };

        var min = samples.Min();
        var max = samples.Max();
        var mean = samples.Average(x => (double)x);
        var variance = samples
            .Select(x =>
            {
                var d = x - mean;
                return d * d;
            })
            .Average();

        return new
        {
            count = samples.Length,
            min,
            max,
            peakToPeakCounts = max - min,
            mean,
            standardDeviationCounts = Math.Sqrt(variance),
            distinctValues = samples.Distinct().Count(),
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
            WriteIndented = true
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
