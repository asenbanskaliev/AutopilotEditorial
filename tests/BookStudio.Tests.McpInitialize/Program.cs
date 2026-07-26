using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BookStudio.Mcp.Transport;

const string secretToken = "mcp-secret-payload-must-not-leak";

try
{
    await RunPrimaryLifecycleAsync(secretToken);
    await RunVersionNegotiationAsync("2025-06-18", "2025-06-18");
    await RunVersionNegotiationAsync("2099-01-01", "2025-11-25");
    await RunOversizeMessageAsync();
    Console.WriteLine(
        "MCP initialize integration PASS: stdio framing, negotiation, lifecycle, errors, diagnostics and EOF verified.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("MCP initialize integration FAIL: " + exception);
    return 1;
}

static async Task RunPrimaryLifecycleAsync(string secretToken)
{
    await using var server = McpChildProcess.Start();

    await server.SendAsync(
        $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{{\"secret\":\"{secretToken}\"");
    using (var parseError = await server.ReadJsonAsync())
    {
        AssertError(parseError.RootElement, -32700);
        Require(parseError.RootElement.GetProperty("id").ValueKind == JsonValueKind.Null, "Parse error id must be null.");
    }

    await server.SendAsync(
        "[{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\",\"params\":{}}]");
    using (var batchError = await server.ReadJsonAsync())
    {
        AssertError(batchError.RootElement, -32600);
    }

    await server.SendAsync(
        "{\"jsonrpc\":\"2.0\",\"id\":\"pre-ping\",\"method\":\"ping\"}");
    using (var ping = await server.ReadJsonAsync())
    {
        AssertStringId(ping.RootElement, "pre-ping");
        Require(!ping.RootElement.GetProperty("result").EnumerateObject().Any(), "Ping result must be empty.");
    }

    await server.SendAsync(
        "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\"}");
    using (var notInitialized = await server.ReadJsonAsync())
    {
        AssertNumericId(notInitialized.RootElement, 3);
        AssertError(notInitialized.RootElement, -32002);
    }

    await server.SendAsync(
        "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{}}}");
    using (var invalidParams = await server.ReadJsonAsync())
    {
        AssertNumericId(invalidParams.RootElement, 4);
        AssertError(invalidParams.RootElement, -32602);
    }

    await server.SendAsync(CurrentInitializeRequest(5));
    using (var initialized = await server.ReadJsonAsync())
    {
        AssertNumericId(initialized.RootElement, 5);
        var result = initialized.RootElement.GetProperty("result");
        Require(
            result.GetProperty("protocolVersion").GetString() == "2025-11-25",
            "Current protocol version was not echoed.");
        Require(
            !result.GetProperty("capabilities").EnumerateObject().Any(),
            "VS-020 must advertise no optional capabilities.");
        var serverInfo = result.GetProperty("serverInfo");
        Require(serverInfo.GetProperty("name").GetString() == "bookstudio", "Server name mismatch.");
        Require(serverInfo.GetProperty("title").GetString() == "BookStudio MCP", "Server title mismatch.");
        Require(
            !string.IsNullOrWhiteSpace(serverInfo.GetProperty("version").GetString()),
            "Server version is missing.");
        Require(
            result.GetProperty("instructions").GetString()?.Contains("No tools", StringComparison.Ordinal) == true,
            "Initialize instructions must disclose the empty feature surface.");
    }

    await server.SendAsync(CurrentInitializeRequest(6));
    using (var duplicateInitialize = await server.ReadJsonAsync())
    {
        AssertNumericId(duplicateInitialize.RootElement, 6);
        AssertError(duplicateInitialize.RootElement, -32600);
    }

    await server.SendAsync(
        "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
    await server.SendAsync(
        "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ping\"}");
    using (var readyPing = await server.ReadJsonAsync())
    {
        AssertNumericId(readyPing.RootElement, 7);
        Require(readyPing.RootElement.TryGetProperty("result", out _), "Initialized notification produced an unexpected response.");
    }

    await server.SendAsync(
        "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/list\"}");
    using (var missingMethod = await server.ReadJsonAsync())
    {
        AssertNumericId(missingMethod.RootElement, 8);
        AssertError(missingMethod.RootElement, -32601);
    }

    await server.SendAsync(
        $"{{\"jsonrpc\":\"2.0\",\"method\":\"notifications/unknown\",\"params\":{{\"secret_token\":\"{secretToken}\"}}}}");
    await server.SendAsync(
        "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"ping\"}");
    using (var notificationPing = await server.ReadJsonAsync())
    {
        AssertNumericId(notificationPing.RootElement, 9);
    }

    await server.SendAsync(
        "{\"jsonrpc\":\"2.0\",\"id\":true,\"method\":\"ping\"}");
    using (var invalidId = await server.ReadJsonAsync())
    {
        AssertError(invalidId.RootElement, -32600);
        Require(invalidId.RootElement.GetProperty("id").ValueKind == JsonValueKind.Null, "Invalid request id must not be reflected.");
    }

    var completed = await server.CloseInputAndWaitAsync();
    Require(completed.ExitCode == 0, "MCP process did not exit successfully after EOF.");
    Require(string.IsNullOrWhiteSpace(completed.RemainingStdout), "stdout contained a banner or unexpected MCP message.");
    ValidateSafeStderr(completed.Stderr, secretToken);
}

static async Task RunVersionNegotiationAsync(string requested, string expected)
{
    await using var server = McpChildProcess.Start();
    await server.SendAsync(
        "{\"jsonrpc\":\"2.0\",\"id\":\"version-init\",\"method\":\"initialize\",\"params\":{" +
        $"\"protocolVersion\":\"{requested}\"," +
        "\"capabilities\":{},\"clientInfo\":{\"name\":\"version-client\",\"version\":\"1.0.0\"}}}");

    using (var response = await server.ReadJsonAsync())
    {
        AssertStringId(response.RootElement, "version-init");
        Require(
            response.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString() == expected,
            $"Negotiated version mismatch for {requested}.");
    }

    var completed = await server.CloseInputAndWaitAsync();
    Require(completed.ExitCode == 0, "Version-negotiation process did not exit cleanly.");
    Require(string.IsNullOrWhiteSpace(completed.RemainingStdout), "Version session emitted unexpected stdout.");
    ValidateSafeStderr(completed.Stderr);
}

static async Task RunOversizeMessageAsync()
{
    await using var server = McpChildProcess.Start();
    await server.SendAsync(new string('x', StdioJsonRpcServer.MaximumMessageBytes + 1));
    using (var response = await server.ReadJsonAsync())
    {
        AssertError(response.RootElement, -32600);
    }

    var completed = await server.CloseInputAndWaitAsync();
    Require(completed.ExitCode == 0, "Oversize-message process did not exit cleanly.");
    Require(string.IsNullOrWhiteSpace(completed.RemainingStdout), "Oversize session emitted unexpected stdout.");
    ValidateSafeStderr(completed.Stderr);
}

static string CurrentInitializeRequest(int id) =>
    "{\"jsonrpc\":\"2.0\",\"id\":" + id +
    ",\"method\":\"initialize\",\"params\":{" +
    "\"protocolVersion\":\"2025-11-25\"," +
    "\"capabilities\":{}," +
    "\"clientInfo\":{\"name\":\"integration-client\",\"title\":\"Integration Client\",\"version\":\"1.0.0\"}}}";

static void AssertError(JsonElement response, int expectedCode)
{
    Require(response.GetProperty("jsonrpc").GetString() == "2.0", "JSON-RPC version mismatch.");
    var error = response.GetProperty("error");
    Require(error.GetProperty("code").GetInt32() == expectedCode, $"Expected error {expectedCode}.");
    Require(!string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()), "Error message is missing.");
}

static void AssertNumericId(JsonElement response, long expected)
{
    Require(response.GetProperty("id").GetInt64() == expected, $"Expected response id {expected}.");
}

static void AssertStringId(JsonElement response, string expected)
{
    Require(response.GetProperty("id").GetString() == expected, $"Expected response id {expected}.");
}

static void ValidateSafeStderr(string stderr, params string[] forbiddenValues)
{
    foreach (var forbidden in forbiddenValues)
    {
        Require(
            !stderr.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            "stderr echoed a sensitive request value.");
    }

    foreach (var line in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
    {
        Require(line.Length <= 96, "stderr diagnostic exceeded its bound.");
        Require(
            line.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'),
            "stderr contained non-diagnostic content.");
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class McpChildProcess : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly Process _process;
    private bool _inputClosed;

    private McpChildProcess(Process process)
    {
        _process = process;
    }

    public static McpChildProcess Start()
    {
        var mcpAssembly = Path.Combine(AppContext.BaseDirectory, "BookStudio.Mcp.dll");
        if (!File.Exists(mcpAssembly))
        {
            throw new FileNotFoundException("BookStudio.Mcp.dll was not copied to the integration output.", mcpAssembly);
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
        startInfo.ArgumentList.Add(mcpAssembly);

        return new McpChildProcess(
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("The MCP child process could not be started."));
    }

    public async Task SendAsync(string message)
    {
        if (_inputClosed)
        {
            throw new InvalidOperationException("MCP stdin is already closed.");
        }

        await _process.StandardInput.WriteLineAsync(message).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    public async Task<JsonDocument> ReadJsonAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var line = await _process.StandardOutput
            .ReadLineAsync(timeout.Token)
            .ConfigureAwait(false);
        if (line is null)
        {
            var stderr = await _process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            throw new InvalidOperationException("MCP process ended before a response. stderr=" + stderr);
        }

        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("stdout contained non-JSON MCP content.", exception);
        }
    }

    public async Task<McpCompletion> CloseInputAndWaitAsync()
    {
        if (!_inputClosed)
        {
            _inputClosed = true;
            _process.StandardInput.Close();
        }

        using var timeout = new CancellationTokenSource(Timeout);
        await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        var remainingStdout = await _process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        var stderr = await _process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        return new McpCompletion(_process.ExitCode, remainingStdout, stderr);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_inputClosed)
        {
            _inputClosed = true;
            _process.StandardInput.Close();
        }

        if (!_process.HasExited)
        {
            try
            {
                using var timeout = new CancellationTokenSource(Timeout);
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
}

internal sealed record McpCompletion(
    int ExitCode,
    string RemainingStdout,
    string Stderr);
