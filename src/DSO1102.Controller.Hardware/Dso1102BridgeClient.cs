using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DSO1102.Controller.Hardware;

internal sealed class Dso1102BridgeClient
{
    private readonly string _bridgeExe;
    private readonly string? _dllPath;

    public Dso1102BridgeClient(string? bridgeExe = null, string? dllPath = null)
    {
        _bridgeExe = ResolveBridgeExecutable(bridgeExe);
        _dllPath = string.IsNullOrWhiteSpace(dllPath)
            ? Environment.GetEnvironmentVariable("DSO1102_SDK_DLL")
            : dllPath;
    }

    public async Task<JsonDocument> RunAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _bridgeExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add(command);

        if (!string.IsNullOrWhiteSpace(_dllPath))
        {
            psi.ArgumentList.Add("--dll");
            psi.ArgumentList.Add(_dllPath);
        }

        using var process = new Process { StartInfo = psi };

        if (!process.Start())
            throw new InvalidOperationException("Could not start the DSO-1102 x86 bridge.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException(
                $"DSO-1102 bridge returned no JSON. Exit code {process.ExitCode}. {stderr}".Trim());

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stdout);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"DSO-1102 bridge returned invalid JSON. Exit code {process.ExitCode}. STDERR: {stderr}",
                ex);
        }

        if (process.ExitCode != 0)
        {
            var error = document.RootElement.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : null;

            document.Dispose();

            throw new InvalidOperationException(
                $"DSO-1102 bridge command '{command}' failed with exit code {process.ExitCode}: {error ?? stderr}");
        }

        if (document.RootElement.TryGetProperty("ok", out var ok) &&
            ok.ValueKind == JsonValueKind.False)
        {
            var error = document.RootElement.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : "Unknown bridge error.";

            document.Dispose();
            throw new InvalidOperationException(error);
        }

        return document;
    }

    private static string ResolveBridgeExecutable(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);
            if (File.Exists(full))
                return full;

            throw new FileNotFoundException("Configured DSO-1102 bridge executable was not found.", full);
        }

        var environment = Environment.GetEnvironmentVariable("DSO1102_BRIDGE_EXE");
        if (!string.IsNullOrWhiteSpace(environment))
        {
            var full = Path.GetFullPath(environment);
            if (File.Exists(full))
                return full;
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "bridge", "DSO1102_Bridge_x86.exe"),
            Path.Combine(AppContext.BaseDirectory, "DSO1102_Bridge_x86.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "DSO1102_Bridge_x86.exe was not found. Set DSO1102_BRIDGE_EXE or place the x86 bridge in a 'bridge' directory next to the x64 controller.");
    }
}
