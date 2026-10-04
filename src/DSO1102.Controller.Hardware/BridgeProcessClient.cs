using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DSO1102.Controller.Hardware;

internal sealed class BridgeProcessClient
{
    private readonly string _bridgeExe;
    private readonly string _vendorDll;

    public BridgeProcessClient(string? bridgeExe = null, string? vendorDll = null)
    {
        _bridgeExe = ResolveBridgeExe(bridgeExe);
        _vendorDll = ResolveVendorDll(vendorDll);
    }

    public string BridgeExe => _bridgeExe;
    public string VendorDll => _vendorDll;

    public async Task<JsonDocument> RunAsync(string command, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _bridgeExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add("--dll");
        startInfo.ArgumentList.Add(_vendorDll);

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
            throw new InvalidOperationException("Could not start the x86 DSO1102 bridge.");

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        });

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException(
                $"DSO1102 bridge returned no JSON. ExitCode={process.ExitCode}. STDERR: {stderr}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stdout);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"DSO1102 bridge returned invalid JSON. ExitCode={process.ExitCode}. STDERR: {stderr}",
                ex);
        }

        var ok = document.RootElement.TryGetProperty("ok", out var okElement) &&
                 okElement.ValueKind == JsonValueKind.True;

        if (process.ExitCode != 0 || !ok)
        {
            var error = document.RootElement.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : null;

            document.Dispose();
            throw new InvalidOperationException(
                $"DSO1102 bridge command '{command}' failed. ExitCode={process.ExitCode}. " +
                $"Error={error ?? "(none)"}. STDERR: {stderr}");
        }

        return document;
    }

    private static string ResolveBridgeExe(string? configured)
    {
        var candidates = new List<string?>();

        if (!string.IsNullOrWhiteSpace(configured))
            candidates.Add(configured);

        candidates.Add(Environment.GetEnvironmentVariable("DSO1102_BRIDGE_EXE"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "bridge", "DSO1102_Bridge_x86.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "DSO1102_Bridge_x86.exe"));

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        throw new FileNotFoundException(
            "The x86 DSO1102 bridge was not found. Set DSO1102_BRIDGE_EXE or use the packaged app artifact containing bridge\\DSO1102_Bridge_x86.exe.");
    }

    private static string ResolveVendorDll(string? configured)
    {
        var candidates = new List<string?>();

        if (!string.IsNullOrWhiteSpace(configured))
            candidates.Add(configured);

        candidates.Add(Environment.GetEnvironmentVariable("DSO1102_SDK_DLL"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "DSO-1102 USB",
            "DSO1102USB.dll"));

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        throw new FileNotFoundException(
            "The original DSO1102USB.dll was not found. Install the vendor software or set DSO1102_SDK_DLL.");
    }
}
