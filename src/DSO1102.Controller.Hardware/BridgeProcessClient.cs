using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DSO1102.Controller.Hardware;

internal sealed class BridgeProcessClient : IAsyncDisposable
{
    private readonly string _bridgeExe;
    private readonly string _vendorDll;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private Process? _process;
    private bool _disposed;
    private readonly TimeSpan _commandTimeout;

    public BridgeProcessClient(string? bridgeExe = null, string? vendorDll = null, TimeSpan? commandTimeout = null)
    {
        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(15);
        if (_commandTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        _bridgeExe = ResolveBridgeExe(bridgeExe);
        _vendorDll = ResolveVendorDll(vendorDll);
    }

    public string BridgeExe => _bridgeExe;
    public string VendorDll => _vendorDll;

    public async Task<JsonDocument> RunAsync(string command, CancellationToken cancellationToken)
    {
        if (command is not ("probe" or "frame-1ms-adc"))
            throw new ArgumentException("Command is not part of the controller protocol.", nameof(command));
        await _gate.WaitAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_commandTimeout);
        var token = timeout.Token;

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            EnsureServerStarted();

            var process = _process
                ?? throw new InvalidOperationException("DSO1102 bridge server was not started.");

            using var registration = token.Register(() => KillProcess(process));

            await process.StandardInput.WriteLineAsync(command.AsMemory(), token);
            await process.StandardInput.FlushAsync(token);

            var response = await process.StandardOutput.ReadLineAsync(token);

            if (string.IsNullOrWhiteSpace(response))
            {
                var exit = process.HasExited ? process.ExitCode.ToString() : "(running)";
                throw new InvalidOperationException(
                    $"DSO1102 bridge server returned no JSON for '{command}'. ExitCode={exit}. STDERR: {ReadStderr()}");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(response);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"DSO1102 bridge server returned invalid JSON for '{command}'. STDERR: {ReadStderr()}",
                    ex);
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new InvalidOperationException("Bridge response must be a JSON object.");
            }

            var ok = document.RootElement.TryGetProperty("ok", out var okElement) &&
                     okElement.ValueKind == JsonValueKind.True;

            if (!ok)
            {
                var error = document.RootElement.TryGetProperty("error", out var errorElement) &&
                    errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString()
                    : null;

                document.Dispose();
                throw new InvalidOperationException(
                    $"DSO1102 bridge command '{command}' failed. Error={error ?? "(none)"}. STDERR: {ReadStderr()}");
            }

            if (!document.RootElement.TryGetProperty("command", out var echoed) ||
                echoed.ValueKind != JsonValueKind.String || echoed.GetString() != command)
            {
                document.Dispose();
                throw new InvalidOperationException("Bridge response command mismatch.");
            }
            if (token.IsCancellationRequested)
            {
                document.Dispose();
                token.ThrowIfCancellationRequested();
            }
            return document;
        }
        catch (Exception ex) when (timeout.IsCancellationRequested)
        {
            TerminateServer();
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("Bridge command cancelled.", ex, cancellationToken);
            throw new TimeoutException($"Bridge command exceeded {_commandTimeout.TotalSeconds:g} seconds.", ex);
        }
        catch
        {
            TerminateServer();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();

        try
        {
            if (_disposed)
                return;
            _disposed = true;
            var process = _process;
            _process = null;

            if (process is null)
                return;

            try
            {
                if (!process.HasExited)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await process.StandardInput.WriteLineAsync("quit".AsMemory(), timeout.Token);
                    await process.StandardInput.FlushAsync(timeout.Token);
                    await process.WaitForExitAsync(timeout.Token);
                }
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }
            finally
            {
                process.Dispose();
            }
        }
        finally
        {
            _gate.Release();
            // Keep the gate usable so repeated/concurrent disposal is idempotent.
        }
    }

    private void EnsureServerStarted()
    {
        if (_process is { HasExited: false })
            return;

        _process?.Dispose();
        _process = null;

        lock (_stderr)
            _stderr.Clear();

        var startInfo = new ProcessStartInfo
        {
            FileName = _bridgeExe,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("server");
        startInfo.ArgumentList.Add("--dll");
        startInfo.ArgumentList.Add(_vendorDll);

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data))
                return;

            lock (_stderr)
            {
                if (_stderr.Length > 16_384)
                    _stderr.Remove(0, _stderr.Length - 8_192);

                _stderr.AppendLine(e.Data);
            }
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Could not start the persistent x86 DSO1102 bridge server.");
        }

        process.BeginErrorReadLine();
        _process = process;
    }

    private void TerminateServer()
    {
        var process = _process;
        _process = null;

        if (process is null)
            return;

        KillProcess(process);
        process.Dispose();
    }

    private static void KillProcess(Process process)
    {
        // Cancellation may interrupt pipe I/O. The caller owns disposal after
        // that I/O unwinds; disposing here races StreamReader/StreamWriter.
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private string ReadStderr()
    {
        lock (_stderr)
            return _stderr.ToString();
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
