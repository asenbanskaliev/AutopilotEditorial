using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BookStudio.Tests.McpConformance;

internal sealed class McpProcessDriver : IAsyncDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(20);

    private readonly Process _process;
    private bool _inputClosed;

    private McpProcessDriver(Process process)
    {
        _process = process;
    }

    public static McpProcessDriver Start(string assemblyName, string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyName);
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException(
                "MCP conformance target is missing from test output.",
                assemblyPath);
        }

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
        startInfo.ArgumentList.Add("--workspace-root");
        startInfo.ArgumentList.Add(workspaceRoot);

        return new McpProcessDriver(
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("MCP conformance target could not be started."));
    }

    public bool HasExited => _process.HasExited;

    public async Task SendRawAsync(string message)
    {
        ObjectDisposedException.ThrowIf(_inputClosed, this);
        await _process.StandardInput.WriteLineAsync(message).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    public Task SendRequestAsync(string id, string method, object? parameters = null)
    {
        var message = parameters is null
            ? new { jsonrpc = "2.0", id, method }
            : new { jsonrpc = "2.0", id, method, @params = parameters };
        return SendRawAsync(JsonSerializer.Serialize(message));
    }

    public Task SendNotificationAsync(string method, object? parameters = null)
    {
        var message = parameters is null
            ? new { jsonrpc = "2.0", method }
            : new { jsonrpc = "2.0", method, @params = parameters };
        return SendRawAsync(JsonSerializer.Serialize(message));
    }

    public async Task<JsonDocument> ReadJsonAsync()
    {
        using var timeout = new CancellationTokenSource(ReadTimeout);
        string? line;
        try
        {
            line = await _process.StandardOutput
                .ReadLineAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException("MCP conformance target did not respond within 10 seconds.", exception);
        }

        if (line is null)
        {
            throw new InvalidOperationException(
                _process.HasExited
                    ? $"MCP conformance target exited prematurely with code {_process.ExitCode}."
                    : "MCP conformance target closed stdout unexpectedly.");
        }

        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("MCP stdout contained non-JSON content.", exception);
        }
    }

    public async Task<McpProcessCompletion> CloseAsync()
    {
        CloseInput();
        using var timeout = new CancellationTokenSource(CloseTimeout);
        try
        {
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException("MCP conformance target did not exit after EOF.", exception);
        }

        var stdout = await _process.StandardOutput
            .ReadToEndAsync(timeout.Token)
            .ConfigureAwait(false);
        var stderr = await _process.StandardError
            .ReadToEndAsync(timeout.Token)
            .ConfigureAwait(false);
        return new McpProcessCompletion(_process.ExitCode, stdout, stderr);
    }

    public async ValueTask DisposeAsync()
    {
        CloseInput();
        if (!_process.HasExited)
        {
            try
            {
                using var timeout = new CancellationTokenSource(CloseTimeout);
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

internal sealed record McpProcessCompletion(
    int ExitCode,
    string RemainingStdout,
    string Stderr);
