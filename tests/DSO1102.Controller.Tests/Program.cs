using DSO1102.Controller.Tests;

// Dependency-free regression runner. It never touches the instrument and runs on Windows and Linux.
//   dotnet run --project tests/DSO1102.Controller.Tests -c Release [-- --analysis-only]
// The same executable doubles as a fake bridge child process ("server" mode).
if (args.FirstOrDefault() == "server")
    return await FakeBridgeServer.RunAsync(args[2]);

var suites = new List<(string Name, Func<Task> Run)>
{
    ("DSP", DspTests.Run),
    ("Payload", PayloadTests.Run),
    ("Scope", ScopeTests.Run),
    ("Presentation", PresentationTests.Run),
};

if (!args.Contains("--analysis-only"))
{
    suites.Add(("Bridge process", async () =>
    {
        var scenario = Path.GetTempFileName();
        try { await BridgeProcessTests.Run(scenario); }
        finally
        {
            File.Delete(scenario);
            File.Delete(scenario + ".log");
        }
    }));
}

var failures = new List<string>();
foreach (var (name, run) in suites)
{
    Console.WriteLine($"== {name}");
    try
    {
        await run();
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
        Console.WriteLine($"FAIL {name}: {ex}");
    }
}

if (failures.Count > 0)
{
    Console.WriteLine($"FAILED: {failures.Count} suite(s); {TestKit.Passed} checks passed before the failures.");
    foreach (var failure in failures)
        Console.WriteLine($"  - {failure}");
    return 1;
}

var skipped = args.Contains("--analysis-only") ? " Bridge process suite explicitly skipped (--analysis-only)." : string.Empty;
Console.WriteLine($"PASS: {TestKit.Passed} regression checks in {suites.Count} suites; no hardware calls.{skipped}");
return 0;
