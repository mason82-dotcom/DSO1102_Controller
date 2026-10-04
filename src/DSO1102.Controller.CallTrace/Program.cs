using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DSO1102.Controller.CallTrace;

internal static class Program
{
    private const uint DebugOnlyThisProcess = 0x00000002;
    private const uint DbgContinue = 0x00010002;
    private const uint DbgExceptionNotHandled = 0x80010001;
    private const uint ExceptionBreakpoint = 0x80000003;
    private const uint LoadDllDebugEvent = 6;
    private const uint ExceptionDebugEvent = 1;
    private const uint ExitProcessDebugEvent = 5;
    private const uint ThreadGetContext = 0x0008;
    private const uint ThreadSetContext = 0x0010;
    private const uint ContextI386 = 0x00010000;
    private const uint ContextControl = ContextI386 | 0x00000001;
    private const uint ContextInteger = ContextI386 | 0x00000002;

    private static int Main(string[] args)
    {
        try
        {
            if (IntPtr.Size != 4)
                throw new InvalidOperationException("Call tracer must run as x86.");

            var exePath = GetArg(args, "--exe")
                ?? @"C:\Program Files (x86)\DSO-1102 USB\DSO-1102 USB.exe";
            var dllPath = GetArg(args, "--dll")
                ?? @"C:\Program Files (x86)\DSO-1102 USB\DSO1102USB.dll";
            var exportName = GetArg(args, "--export") ?? "dsoGetChannelData";
            var argCount = int.TryParse(GetArg(args, "--args"), out var parsed) ? parsed : 8;

            if (!File.Exists(exePath))
                throw new FileNotFoundException("Vendor application not found.", exePath);
            if (!File.Exists(dllPath))
                throw new FileNotFoundException("Vendor DLL not found.", dllPath);
            if (argCount is < 1 or > 16)
                throw new ArgumentOutOfRangeException(nameof(argCount), "Argument count must be 1..16.");

            var exportRva = PeExports.GetExportRva(dllPath, exportName);
            if (exportRva == 0)
                throw new EntryPointNotFoundException($"Export '{exportName}' not found in '{dllPath}'.");

            var result = TraceOneCall(exePath, dllPath, exportName, exportRva, argCount);
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return result.Captured ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                ok = false,
                error = ex.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    private static TraceResult TraceOneCall(
        string exePath,
        string dllPath,
        string exportName,
        uint exportRva,
        int argCount)
    {
        var startup = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>()
        };

        var commandLine = new StringBuilder($"\"{exePath}\"");

        if (!CreateProcessW(
                exePath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                DebugOnlyThisProcess,
                IntPtr.Zero,
                Path.GetDirectoryName(exePath),
                ref startup,
                out var processInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW failed.");
        }

        DebugSetProcessKillOnExit(false);

        IntPtr breakpointAddress = IntPtr.Zero;
        byte originalByte = 0;
        bool breakpointInstalled = false;
        bool captured = false;
        uint[] rawStack = [];
        List<ArgumentSnapshot> snapshots = [];
        string? loadedDll = null;

        try
        {
            while (WaitForDebugEvent(out var debugEvent, 30_000))
            {
                var continueStatus = DbgContinue;
                var shouldExitLoop = false;

                try
                {
                    if (debugEvent.dwDebugEventCode == LoadDllDebugEvent)
                    {
                        var info = debugEvent.u.LoadDll;
                        var path = TryGetFileNameFromHandle(info.hFile);

                        if (!string.IsNullOrWhiteSpace(path) &&
                            string.Equals(
                                Path.GetFileName(path),
                                Path.GetFileName(dllPath),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            loadedDll = path;
                            breakpointAddress = IntPtr.Add(info.lpBaseOfDll, checked((int)exportRva));

                            var one = new byte[1];
                            EnsureRead(processInfo.hProcess, breakpointAddress, one);
                            originalByte = one[0];

                            EnsureWrite(processInfo.hProcess, breakpointAddress, [0xCC]);
                            FlushInstructionCache(processInfo.hProcess, breakpointAddress, (UIntPtr)1);
                            breakpointInstalled = true;
                        }

                        if (info.hFile != IntPtr.Zero)
                            CloseHandle(info.hFile);
                    }
                    else if (debugEvent.dwDebugEventCode == ExceptionDebugEvent)
                    {
                        var exception = debugEvent.u.Exception;
                        var address = exception.ExceptionRecord.ExceptionAddress;

                        if (exception.ExceptionRecord.ExceptionCode == ExceptionBreakpoint &&
                            breakpointInstalled &&
                            address == breakpointAddress)
                        {
                            var thread = OpenThread(
                                ThreadGetContext | ThreadSetContext,
                                false,
                                debugEvent.dwThreadId);

                            if (thread == IntPtr.Zero)
                                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenThread failed.");

                            try
                            {
                                var context = CONTEXT32.Create();
                                context.ContextFlags = ContextControl | ContextInteger;

                                if (!GetThreadContext(thread, ref context))
                                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GetThreadContext failed.");

                                var stackBytes = new byte[(argCount + 1) * 4];
                                EnsureRead(processInfo.hProcess, new IntPtr(unchecked((int)context.Esp)), stackBytes);

                                rawStack = Enumerable.Range(0, argCount + 1)
                                    .Select(i => BitConverter.ToUInt32(stackBytes, i * 4))
                                    .ToArray();

                                for (var i = 1; i <= argCount; i++)
                                {
                                    var value = rawStack[i];
                                    snapshots.Add(new ArgumentSnapshot
                                    {
                                        Index = i,
                                        Value = value,
                                        Hex = $"0x{value:X8}",
                                        PointerPreviewHex = TryReadPointerPreview(processInfo.hProcess, value, 64)
                                    });
                                }

                                EnsureWrite(processInfo.hProcess, breakpointAddress, [originalByte]);
                                FlushInstructionCache(processInfo.hProcess, breakpointAddress, (UIntPtr)1);

                                context.Eip = unchecked((uint)breakpointAddress.ToInt32());
                                if (!SetThreadContext(thread, ref context))
                                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetThreadContext failed.");

                                breakpointInstalled = false;
                                captured = true;
                                shouldExitLoop = true;
                            }
                            finally
                            {
                                CloseHandle(thread);
                            }
                        }
                        else if (exception.ExceptionRecord.ExceptionCode == ExceptionBreakpoint)
                        {
                            // Windows raises an initial breakpoint for every debugged process.
                            // It is not an application fault and must be consumed by the debugger.
                            continueStatus = DbgContinue;
                        }
                        else
                        {
                            continueStatus = DbgExceptionNotHandled;
                        }
                    }
                    else if (debugEvent.dwDebugEventCode == ExitProcessDebugEvent)
                    {
                        shouldExitLoop = true;
                    }
                }
                finally
                {
                    ContinueDebugEvent(debugEvent.dwProcessId, debugEvent.dwThreadId, continueStatus);
                }

                if (shouldExitLoop)
                    break;
            }
        }
        finally
        {
            if (breakpointInstalled && breakpointAddress != IntPtr.Zero)
            {
                try
                {
                    EnsureWrite(processInfo.hProcess, breakpointAddress, [originalByte]);
                    FlushInstructionCache(processInfo.hProcess, breakpointAddress, (UIntPtr)1);
                }
                catch
                {
                    // Best effort only while unwinding.
                }
            }

            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
        }

        return new TraceResult
        {
            Ok = captured,
            Captured = captured,
            Export = exportName,
            ExportRva = $"0x{exportRva:X8}",
            VendorExe = exePath,
            VendorDll = dllPath,
            LoadedDll = loadedDll,
            ReturnAddress = rawStack.Length > 0 ? $"0x{rawStack[0]:X8}" : null,
            Arguments = snapshots,
            Instructions = captured
                ? "Call captured. Vendor application continues normally after the one-shot breakpoint."
                : "No matching call was captured. Run/acquire a waveform in the vendor application while the tracer is active."
        };
    }

    private static string? TryReadPointerPreview(IntPtr process, uint value, int length)
    {
        if (value < 0x00010000 || value >= 0xFFF00000)
            return null;

        var bytes = new byte[length];
        if (!ReadProcessMemory(process, new IntPtr(unchecked((int)value)), bytes, bytes.Length, out var read) ||
            read == IntPtr.Zero)
            return null;

        var count = Math.Min(length, read.ToInt32());
        return Convert.ToHexString(bytes.AsSpan(0, count));
    }

    private static string? TryGetFileNameFromHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return null;

        var buffer = new StringBuilder(1024);
        var len = GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, 0);
        if (len == 0 || len >= buffer.Capacity)
            return null;

        var value = buffer.ToString();
        const string prefix = @"\\?\";
        return value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;
    }

    private static void EnsureRead(IntPtr process, IntPtr address, byte[] buffer)
    {
        if (!ReadProcessMemory(process, address, buffer, buffer.Length, out var read) ||
            read.ToInt64() != buffer.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"ReadProcessMemory failed at 0x{address.ToInt64():X}.");
        }
    }

    private static void EnsureWrite(IntPtr process, IntPtr address, byte[] bytes)
    {
        if (!WriteProcessMemory(process, address, bytes, bytes.Length, out var written) ||
            written.ToInt64() != bytes.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"WriteProcessMemory failed at 0x{address.ToInt64():X}.");
        }
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WaitForDebugEvent(out DEBUG_EVENT lpDebugEvent, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ContinueDebugEvent(uint dwProcessId, uint dwThreadId, uint dwContinueStatus);

    [DllImport("kernel32.dll")]
    private static extern bool DebugSetProcessKillOnExit(bool killOnExit);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadContext(IntPtr hThread, ref CONTEXT32 lpContext);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadContext(IntPtr hThread, ref CONTEXT32 lpContext);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        [Out] byte[] lpBuffer,
        int dwSize,
        out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        byte[] lpBuffer,
        int nSize,
        out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        IntPtr hFile,
        StringBuilder lpszFilePath,
        int cchFilePath,
        uint dwFlags);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private unsafe struct EXCEPTION_RECORD32
    {
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public IntPtr ExceptionRecord;
        public IntPtr ExceptionAddress;
        public uint NumberParameters;
        public fixed uint ExceptionInformation[15];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct EXCEPTION_DEBUG_INFO
    {
        public EXCEPTION_RECORD32 ExceptionRecord;
        public uint dwFirstChance;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct LOAD_DLL_DEBUG_INFO
    {
        public IntPtr hFile;
        public IntPtr lpBaseOfDll;
        public uint dwDebugInfoFileOffset;
        public uint nDebugInfoSize;
        public IntPtr lpImageName;
        public ushort fUnicode;
    }

    [StructLayout(LayoutKind.Explicit, Pack = 4)]
    private struct DEBUG_EVENT_UNION
    {
        [FieldOffset(0)] public EXCEPTION_DEBUG_INFO Exception;
        [FieldOffset(0)] public LOAD_DLL_DEBUG_INFO LoadDll;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DEBUG_EVENT
    {
        public uint dwDebugEventCode;
        public uint dwProcessId;
        public uint dwThreadId;
        public DEBUG_EVENT_UNION u;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private unsafe struct FLOATING_SAVE_AREA
    {
        public uint ControlWord;
        public uint StatusWord;
        public uint TagWord;
        public uint ErrorOffset;
        public uint ErrorSelector;
        public uint DataOffset;
        public uint DataSelector;
        public fixed byte RegisterArea[80];
        public uint Cr0NpxState;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private unsafe struct CONTEXT32
    {
        public uint ContextFlags;
        public uint Dr0;
        public uint Dr1;
        public uint Dr2;
        public uint Dr3;
        public uint Dr6;
        public uint Dr7;
        public FLOATING_SAVE_AREA FloatSave;
        public uint SegGs;
        public uint SegFs;
        public uint SegEs;
        public uint SegDs;
        public uint Edi;
        public uint Esi;
        public uint Ebx;
        public uint Edx;
        public uint Ecx;
        public uint Eax;
        public uint Ebp;
        public uint Eip;
        public uint SegCs;
        public uint EFlags;
        public uint Esp;
        public uint SegSs;
        public fixed byte ExtendedRegisters[512];

        public static CONTEXT32 Create() => default;
    }

    private sealed class TraceResult
    {
        public bool Ok { get; init; }
        public bool Captured { get; init; }
        public string Export { get; init; } = "";
        public string ExportRva { get; init; } = "";
        public string VendorExe { get; init; } = "";
        public string VendorDll { get; init; } = "";
        public string? LoadedDll { get; init; }
        public string? ReturnAddress { get; init; }
        public List<ArgumentSnapshot> Arguments { get; init; } = [];
        public string Instructions { get; init; } = "";
    }

    private sealed class ArgumentSnapshot
    {
        public int Index { get; init; }
        public uint Value { get; init; }
        public string Hex { get; init; } = "";
        public string? PointerPreviewHex { get; init; }
    }

    private static class PeExports
    {
        public static uint GetExportRva(string path, string name)
        {
            var bytes = File.ReadAllBytes(path);
            var pe = BitConverter.ToInt32(bytes, 0x3C);
            var sectionCount = BitConverter.ToUInt16(bytes, pe + 6);
            var optionalSize = BitConverter.ToUInt16(bytes, pe + 20);
            var optional = pe + 24;
            var magic = BitConverter.ToUInt16(bytes, optional);
            var dataDirectory = optional + (magic == 0x10B ? 96 : 112);
            var exportDirRva = BitConverter.ToUInt32(bytes, dataDirectory);
            if (exportDirRva == 0)
                return 0;

            var sections = optional + optionalSize;
            int RvaToOffset(uint rva)
            {
                for (var i = 0; i < sectionCount; i++)
                {
                    var s = sections + i * 40;
                    var virtualSize = BitConverter.ToUInt32(bytes, s + 8);
                    var virtualAddress = BitConverter.ToUInt32(bytes, s + 12);
                    var rawSize = BitConverter.ToUInt32(bytes, s + 16);
                    var rawPointer = BitConverter.ToUInt32(bytes, s + 20);
                    var size = Math.Max(virtualSize, rawSize);

                    if (rva >= virtualAddress && rva < virtualAddress + size)
                        return checked((int)(rawPointer + (rva - virtualAddress)));
                }

                return checked((int)rva);
            }

            var export = RvaToOffset(exportDirRva);
            var numberOfNames = BitConverter.ToUInt32(bytes, export + 24);
            var functionsRva = BitConverter.ToUInt32(bytes, export + 28);
            var namesRva = BitConverter.ToUInt32(bytes, export + 32);
            var ordinalsRva = BitConverter.ToUInt32(bytes, export + 36);

            var functions = RvaToOffset(functionsRva);
            var names = RvaToOffset(namesRva);
            var ordinals = RvaToOffset(ordinalsRva);

            for (var i = 0; i < numberOfNames; i++)
            {
                var nameRva = BitConverter.ToUInt32(bytes, names + i * 4);
                var offset = RvaToOffset(nameRva);
                var end = offset;
                while (end < bytes.Length && bytes[end] != 0)
                    end++;

                var exportName = Encoding.ASCII.GetString(bytes, offset, end - offset);
                if (!string.Equals(exportName, name, StringComparison.Ordinal))
                    continue;

                var ordinal = BitConverter.ToUInt16(bytes, ordinals + i * 2);
                return BitConverter.ToUInt32(bytes, functions + ordinal * 4);
            }

            return 0;
        }
    }
}
