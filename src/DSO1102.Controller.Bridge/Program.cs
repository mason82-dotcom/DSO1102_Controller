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
                "exports" => CheckExports(dllPath),
                _ => Fail($"Unknown command '{command}'. Supported: probe, exports.")
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
