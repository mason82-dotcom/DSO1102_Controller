using System.Text.Json;

namespace DSO1102.Controller.Tests;

/// <summary>
/// Deterministic stand-in for the x86 bridge: the test executable re-launches itself with
/// "server --dll &lt;scenario file&gt;". The scenario file selects the behaviour; every received
/// command is appended to "&lt;scenario&gt;.log" so tests can assert the command sequence.
/// </summary>
internal static class FakeBridgeServer
{
    public static async Task<int> RunAsync(string scenarioPath)
    {
        var mode = await File.ReadAllTextAsync(scenarioPath);
        while (Console.ReadLine() is { } command && command != "quit")
        {
            await File.AppendAllTextAsync(scenarioPath + ".log", command + "\n");
            switch (mode)
            {
                case "hang":
                    await Task.Delay(Timeout.Infinite);
                    break;
                case "exit":
                    return 0;
                case "invalid":
                    Console.WriteLine("not-json");
                    continue;
                case "array":
                    Console.WriteLine("[]");
                    continue;
                case "frame" when command == "frame-1ms-adc" || command.StartsWith("frame-1ms-analog ", StringComparison.Ordinal):
                    Console.WriteLine(Frame(command));
                    continue;
                case "frame" when command == "probe":
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        ok = true,
                        command,
                        devices = new[] { new { present = true, fpgaVersion = 1 } }
                    }));
                    continue;
                case "slow":
                    await Task.Delay(50);
                    break;
            }

            Console.WriteLine(JsonSerializer.Serialize(new { ok = mode != "fail", command = mode == "mismatch" ? "wrong" : command }));
        }

        return 0;
    }

    /// <summary>10,240 samples of code 128 per channel with one 255 spike at sample 9,500.</summary>
    private static string Frame(string command)
    {
        var bytes = new byte[10240 * 2];
        for (var i = 0; i < bytes.Length; i += 2) bytes[i] = 128;
        bytes[19000] = 255;
        return JsonSerializer.Serialize(new
        {
            ok = true,
            command,
            profile = new { decodedOutputReferenceRateHz = 5_000_000 },
            capture = new
            {
                adcPayload = new
                {
                    encoding = "u16le-base64",
                    sampleCountPerChannel = 10240,
                    channel1 = Convert.ToBase64String(bytes),
                    channel2 = Convert.ToBase64String(bytes)
                }
            }
        });
    }
}
