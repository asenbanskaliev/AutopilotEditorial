using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BookStudio.Tests.McpSecuritySandbox;

internal sealed class SandboxProcessDriver : IAsyncDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(20);

    private readonly Process _process;
    private bool _inputClosed;

    private SandboxProcessDriver(Process process)
    {
        _process = process;
    }

    public static SandboxProcessDriver Start(string assemblyName, params string[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        ArgumentNullException.ThrowIfNull(arguments);

        var assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyName);
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException(
                "MCP sandbox target is missing from test output.",
                assemblyPath);
        }

        var startInfo = CreateStartInfo(assemblyPath, arguments);
        return new SandboxProcessDriver(
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("MCP sandbox target could not be started."));
    }

    public static async Task<SandboxProcessCompletion> RunToExitAsync(
        string assemblyName,
        params string[] arguments)
    {
        await using var driver = Start(assemblyName, arguments);
        return await driver.CloseAsync().ConfigureAwait(false);
    }

    public async Task SendRequestAsync(string id, string method, object? parameters = null)
    {
        var payload = parameters is null
            ? JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method })
            : JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        await SendRawAsync(payload).ConfigureAwait(false);
    }

    public async Task SendNotificationAsync(string method, object? parameters = null)
    {
        var payload = parameters is null
            ? JsonSerializer.Serialize(new { jsonrpc = "2.0", method })
            : JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters });
        await SendRawAsync(payload).ConfigureAwait(false);
    }

    public async Task SendRawAsync(string payload)
    {
        ObjectDisposedException.ThrowIf(_inputClosed, this);
        await _process.StandardInput.WriteLineAsync(payload).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    public async Task<JsonDocument> ReadJsonAsync()
    {
        using var timeout = new CancellationTokenSource(ReadTimeout);
        string? line;
        try
        {
            line = await _process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException("MCP sandbox target did not respond within 10 seconds.", exception);
        }

        if (line is null)
        {
            throw new InvalidOperationException(
                _process.HasExited
                    ? $"MCP sandbox target exited prematurely with code {_process.ExitCode}."
                    : "MCP sandbox target closed stdout unexpectedly.");
        }

        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("MCP sandbox stdout contained non-JSON content.", exception);
        }
    }

    public async Task<SandboxProcessCompletion> CloseAsync()
    {
        CloseInput();
        using var timeout = new CancellationTokenSource(ExitTimeout);
        try
        {
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException("MCP sandbox target did not exit after EOF.", exception);
        }

        var stdout = await _process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        var stderr = await _process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        return new SandboxProcessCompletion(_process.ExitCode, stdout, stderr);
    }

    public async ValueTask DisposeAsync()
    {
        CloseInput();
        if (!_process.HasExited)
        {
            try
            {
                using var timeout = new CancellationTokenSource(ExitTimeout);
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        _process.Dispose();
    }

    private static ProcessStartInfo CreateStartInfo(
        string assemblyPath,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add(assemblyPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private void CloseInput()
    {
        if (_inputClosed)
        {
            return;
        }
        _inputClosed = true;
        _process.StandardInput.Close();
    }
}

internal sealed record SandboxProcessCompletion(
    int ExitCode,
    string RemainingStdout,
    string Stderr);
