using System.Runtime.InteropServices;
using System.Text.Json;
using DSO1102.Controller.Core.Models;

namespace DSO1102.Controller.Bridge;

// Entry point and line protocol: one command per stdin line, one JSON object per stdout line.
internal static partial class Program
{
    private static bool _compactJson;

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

    private static int ExecuteCommand(string command, string dllPath)
    {
        if (command.StartsWith(AnalogFramePrefix, StringComparison.Ordinal))
        {
            if (!AnalogRequest.TryParse(command[AnalogFramePrefix.Length..], out var analog, out var error))
                return Fail(error!);

            return CaptureGroundBaselineV2(dllPath, command, groundReference: false, timeBaseCode: 16, timeBaseLabel: "1 ms/div",
                decodedOutputReferenceRateHz: 5_000_000, selfInitializeTimeBase: true, emitAdcPayload: true,
                selfInitializeAnalog: true, analog: analog);
        }

        return ExecuteNamedCommand(command, dllPath);
    }

    private static int ExecuteNamedCommand(string command, string dllPath) =>
        command switch
        {
            "sdk-info" => InspectSdk(dllPath),
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
            _ => Fail($"Unknown command '{command}'. Supported: probe, info, arm, force, capture-raw, capture-gnd-v2, capture-400us, capture-1ms, capture-2ms, capture-4ms, self-init-400us, self-init-1ms, self-init-2ms, self-init-4ms, frame-400us-adc, frame-1ms-adc, frame-1ms-analog <ch1Range 5..7> <ch2Range 5..7> <ch1Coupling 0|1> <ch2Coupling 0|1>, self-init-1ms-analog, exports.")
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

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    private static void WriteJson(object value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, _compactJson ? CompactJson : IndentedJson));

    private static int Fail(string message)
    {
        WriteJson(new { ok = false, error = message });
        return 1;
    }
}
