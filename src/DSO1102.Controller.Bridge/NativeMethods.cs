using System.Runtime.InteropServices;
using System.Text.Json;
using DSO1102.Controller.Core.Models;

namespace DSO1102.Controller.Bridge;

// Vendor DLL ABI (stdcall delegates reconstructed in docs/DLL_COMPLETE_ANALYSIS.md) and module loading.
internal static partial class Program
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

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, [MarshalAs(UnmanagedType.LPStr)] string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    private sealed class VendorLibrary : IDisposable
    {
        private IntPtr _module;

        private VendorLibrary(IntPtr module) => _module = module;

        public static VendorLibrary Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("DSO1102 vendor DLL was not found.", path);

            VendorSdkIdentity.RequireKnownBinary(path);
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
