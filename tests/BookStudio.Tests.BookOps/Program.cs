using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BookStudio.Infrastructure.Persistence.Sqlite;

var missingRoot = Path.Combine(
    Path.GetTempPath(),
    "bookstudio-ops-missing-" + Guid.NewGuid().ToString("N"));
var readyRoot = Path.Combine(
    Path.GetTempPath(),
    "bookstudio-ops-ready-" + Guid.NewGuid().ToString("N"));

try
{
    await VerifyMissingWorkspaceAsync(missingRoot);
    await InitializeReadyWorkspaceAsync(readyRoot);
    await VerifyReadyWorkspaceAsync(readyRoot);
    Console.WriteLine("BOOK_OPS_INTEGRATION_PASS");
    return 0;
}
finally
{
    TryDelete(missingRoot);
    TryDelete(readyRoot);
}

static async Task VerifyMissingWorkspaceAsync(string workspaceRoot)
{
    Require(!Directory.Exists(workspaceRoot), "Missing workspace already exists.");
    await using var ops = OpsChildProcess.Start(workspaceRoot);
    await ops.SendRequestAsync(
        1,
        "initialize",
        new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "ops-missing-client", version = "1.0.0" },
        });
    using (var initialize = await ops.ReadJsonAsync())
    {
        var result = initialize.RootElement.GetProperty("result");
        var server = result.GetProperty("serverInfo");
        Require(server.GetProperty("name").GetString() == "bookstudio-ops", "Ops server name mismatch.");
        Require(server.GetProperty("title").GetString() == "BookStudio Operations MCP", "Ops server title mismatch.");
        var capabilities = result.GetProperty("capabilities");
        var names = capabilities
            .EnumerateObject()
            .Select(property => property.Name)
            .Order()
            .ToArray();
        Require(names.SequenceEqual(new[] { "prompts", "resources", "tools" }), "Ops capabilities are not exact.");
        Require(!capabilities.GetProperty("prompts").GetProperty("listChanged").GetBoolean(), "Ops prompts.listChanged must be false.");
    }
    await ops.SendNotificationAsync("notifications/initialized", new { });

    await ops.SendRequestAsync(2, "tools/list", new { });
    using (var toolsResponse = await ops.ReadJsonAsync())
    {
        var tools = toolsResponse.RootElement.GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .ToArray();
        Require(tools.Length == 2, "Ops tools/list must expose exactly two tools.");
        Require(
            tools.Select(tool => tool.GetProperty("name").GetString())
                .SequenceEqual(new[] { "book.ops.diagnostics", "book.ops.status" }),
            "Ops tool ordering mismatch.");
        foreach (var tool in tools)
        {
            var annotations = tool.GetProperty("annotations");
            Require(annotations.GetProperty("readOnlyHint").GetBoolean(), "Ops tool is not read-only.");
            Require(annotations.GetProperty("idempotentHint").GetBoolean(), "Ops tool is not idempotent.");
            Require(!annotations.GetProperty("destructiveHint").GetBoolean(), "Ops tool is destructive.");
        }
        Require(
            !toolsResponse.RootElement.GetRawText().Contains("book.autopilot.start", StringComparison.Ordinal),
            "Reserved Autopilot tool was advertised.");
    }

    var resourceUris = new List<string>();
    string? cursor = null;
    var resourceRequestId = 1000;
    do
    {
        if (cursor is null)
        {
            await ops.SendRequestAsync(resourceRequestId++, "resources/list", new { });
        }
        else
        {
            await ops.SendRequestAsync(resourceRequestId++, "resources/list", new { cursor });
        }
        using var resourcePage = await ops.ReadJsonAsync();
        var result = resourcePage.RootElement.GetProperty("result");
        var page = result.GetProperty("resources").EnumerateArray().ToArray();
        Require(page.Length > 0, "Ops resource pagination returned an empty page.");
        resourceUris.AddRange(page.Select(item => item.GetProperty("uri").GetString()!));
        cursor = result.TryGetProperty("nextCursor", out var nextCursor)
            ? nextCursor.GetString()
            : null;
        Require(resourceRequestId <= 1020, "Ops resource pagination did not terminate.");
    }
    while (cursor is not null);
    Require(resourceUris.Count == 7, "Ops merged resource count mismatch.");
    Require(resourceUris.SequenceEqual(resourceUris.OrderBy(uri => uri, StringComparer.Ordinal)), "Ops resources are not ordinally sorted.");
    Require(resourceUris.Contains("book://ops/capabilities"), "Ops capability resource is missing.");
    Require(resourceUris.Contains("book://prompts/book-ops/inspect-readiness/v1"), "Ops prompt resource is missing.");
    Require(resourceUris.Contains("book://security/sandbox-policy"), "Ops sandbox policy resource is missing.");

    await ops.SendRequestAsync(5, "resources/read", new { uri = "book://ops/capabilities" });
    string capabilityResource;
    using (var resource = await ops.ReadJsonAsync())
    {
        var content = resource.RootElement.GetProperty("result").GetProperty("contents")[0];
        Require(content.GetProperty("mimeType").GetString() == "application/json", "Ops capability media type mismatch.");
        capabilityResource = content.GetProperty("text").GetString()!;
        using var document = JsonDocument.Parse(capabilityResource);
        var capabilities = document.RootElement.GetProperty("capabilities").EnumerateArray().ToArray();
        Require(capabilities.Length == 15, "Ops capability count mismatch.");
        Require(
            capabilities.Any(capability =>
                capability.GetProperty("id").GetString() == "autopilot.workflow" &&
                capability.GetProperty("status").GetString() == "reserved"),
            "Autopilot workflow capability is not reserved.");
    }

    await CallToolAsync(ops, 6, "book.ops.status");
    using (var statusResponse = await ops.ReadJsonAsync())
    {
        var result = statusResponse.RootElement.GetProperty("result");
        Require(!result.GetProperty("isError").GetBoolean(), "Missing-workspace status returned a tool error.");
        var data = result.GetProperty("structuredContent").GetProperty("data");
        Require(data.GetProperty("status").GetString() == "notReady", "Missing workspace status mismatch.");
        Require(data.GetProperty("probeCount").GetInt32() == 1, "Missing workspace probe count mismatch.");
        Require(data.GetProperty("readyProbeCount").GetInt32() == 0, "Missing workspace ready count mismatch.");
        Require(data.GetProperty("autopilotAvailability").GetString() == "unavailable", "Autopilot availability mismatch.");
        Require(
            data.GetProperty("unreadyProbes").EnumerateArray()
                .Select(item => item.GetString())
                .SequenceEqual(new[] { "workspace-database" }),
            "Missing workspace unready probe mismatch.");
        AssertNoLeaks(statusResponse.RootElement.GetRawText(), workspaceRoot);
    }

    await CallToolAsync(ops, 7, "book.ops.diagnostics");
    using (var diagnosticsResponse = await ops.ReadJsonAsync())
    {
        var result = diagnosticsResponse.RootElement.GetProperty("result");
        Require(!result.GetProperty("isError").GetBoolean(), "Missing-workspace diagnostics returned a tool error.");
        var data = result.GetProperty("structuredContent").GetProperty("data");
        Require(data.GetProperty("status").GetString() == "notReady", "Missing diagnostics status mismatch.");
        var check = data.GetProperty("checks")[0];
        Require(check.GetProperty("name").GetString() == "workspace-database", "Missing diagnostics probe name mismatch.");
        Require(!check.GetProperty("ready").GetBoolean(), "Missing diagnostics probe unexpectedly ready.");
        Require(check.GetProperty("status").GetString() == "missing", "Missing diagnostics probe status mismatch.");
        var recommendations = data.GetProperty("recommendations").EnumerateArray()
            .Select(item => item.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Require(recommendations.Contains("initialize_workspace_via_control_center"), "Missing workspace recommendation absent.");
        Require(recommendations.Contains("complete_f3_opencode_before_model_sessions"), "OpenCode recommendation absent.");
        Require(recommendations.Contains("complete_f4_autopilot_before_workflow_controls"), "Autopilot recommendation absent.");
        AssertCapabilityParity(capabilityResource, data.GetProperty("capabilities"));
        AssertNoLeaks(diagnosticsResponse.RootElement.GetRawText(), workspaceRoot);
    }

    Require(!Directory.Exists(workspaceRoot), "Ops status or diagnostics created a missing workspace.");

    await ops.SendRequestAsync(
        8,
        "tools/call",
        new
        {
            name = "book.ops.status",
            arguments = new { unexpected = true },
        });
    using (var invalid = await ops.ReadJsonAsync())
    {
        AssertJsonRpcError(invalid.RootElement, -32602);
    }

    var completion = await ops.CloseInputAndWaitAsync();
    Require(completion.ExitCode == 0, "Missing-workspace ops process did not exit cleanly.");
    Require(string.IsNullOrWhiteSpace(completion.RemainingStdout), "Missing-workspace ops stdout contained extra content.");
    ValidateSafeStderr(completion.Stderr, workspaceRoot, "bookstudio.db");
}

static async Task InitializeReadyWorkspaceAsync(string workspaceRoot)
{
    Require(!Directory.Exists(workspaceRoot), "Ready fixture workspace already exists.");
    await using var database = new SqliteWorkspaceDatabase(
        SqliteWorkspaceOptions.Create(workspaceRoot));
    var health = await database.InitializeAsync().ConfigureAwait(false);
    Require(health.IsHealthy, "Real SQLite fixture did not initialize healthy.");
}

static async Task VerifyReadyWorkspaceAsync(string workspaceRoot)
{
    Require(Directory.Exists(workspaceRoot), "Ready workspace is missing.");
    await using var ops = OpsChildProcess.Start(workspaceRoot);
    await InitializeAndReadyAsync(ops, 1, "ops-ready-client");

    await CallToolAsync(ops, 2, "book.ops.status");
    using (var statusResponse = await ops.ReadJsonAsync())
    {
        var data = statusResponse.RootElement.GetProperty("result")
            .GetProperty("structuredContent")
            .GetProperty("data");
        Require(data.GetProperty("status").GetString() == "ready", "Ready workspace status mismatch.");
        Require(data.GetProperty("probeCount").GetInt32() == 1, "Ready probe count mismatch.");
        Require(data.GetProperty("readyProbeCount").GetInt32() == 1, "Ready probe total mismatch.");
        Require(data.GetProperty("unreadyProbes").GetArrayLength() == 0, "Ready status returned unready probes.");
        var reserved = data.GetProperty("reservedComponents").EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Require(reserved.Length == 6, "Reserved operations component count mismatch.");
        AssertNoLeaks(statusResponse.RootElement.GetRawText(), workspaceRoot);
    }

    await ops.SendRequestAsync(3, "resources/read", new { uri = "book://ops/capabilities" });
    string capabilityResource;
    using (var resource = await ops.ReadJsonAsync())
    {
        capabilityResource = resource.RootElement.GetProperty("result")
            .GetProperty("contents")[0]
            .GetProperty("text")
            .GetString()!;
    }

    await CallToolAsync(ops, 4, "book.ops.diagnostics");
    using (var diagnosticsResponse = await ops.ReadJsonAsync())
    {
        var data = diagnosticsResponse.RootElement.GetProperty("result")
            .GetProperty("structuredContent")
            .GetProperty("data");
        Require(data.GetProperty("status").GetString() == "ready", "Ready diagnostics status mismatch.");
        var check = data.GetProperty("checks")[0];
        Require(check.GetProperty("ready").GetBoolean(), "Ready database probe is not ready.");
        Require(check.GetProperty("status").GetString() == "ready", "Ready database probe status mismatch.");
        Require(check.GetProperty("appliedMigrationCount").GetInt32() > 0, "Migration count was not reported.");
        Require(check.GetProperty("latestMigrationVersion").GetInt32() > 0, "Latest migration version was not reported.");
        AssertCapabilityParity(capabilityResource, data.GetProperty("capabilities"));
        AssertNoLeaks(diagnosticsResponse.RootElement.GetRawText(), workspaceRoot);
    }

    var inventoryAfterWarmup = Inventory(workspaceRoot);

    await CallToolAsync(ops, 5, "book.ops.status");
    using (var repeatedStatus = await ops.ReadJsonAsync())
    {
        Require(
            repeatedStatus.RootElement.GetProperty("result")
                .GetProperty("structuredContent")
                .GetProperty("data")
                .GetProperty("status")
                .GetString() == "ready",
            "Repeated status changed unexpectedly.");
    }
    await CallToolAsync(ops, 6, "book.ops.diagnostics");
    using (var repeatedDiagnostics = await ops.ReadJsonAsync())
    {
        Require(
            repeatedDiagnostics.RootElement.GetProperty("result")
                .GetProperty("structuredContent")
                .GetProperty("data")
                .GetProperty("status")
                .GetString() == "ready",
            "Repeated diagnostics changed unexpectedly.");
    }
    Require(
        inventoryAfterWarmup.SequenceEqual(Inventory(workspaceRoot)),
        "Repeated ops status or diagnostics mutated the workspace inventory.");

    await ops.SendRequestAsync(
        7,
        "tools/call",
        new
        {
            name = "book.autopilot.start",
            arguments = new { },
        });
    using (var reserved = await ops.ReadJsonAsync())
    {
        AssertJsonRpcError(reserved.RootElement, -32602);
    }

    var completion = await ops.CloseInputAndWaitAsync();
    Require(completion.ExitCode == 0, "Ready-workspace ops process did not exit cleanly.");
    Require(string.IsNullOrWhiteSpace(completion.RemainingStdout), "Ready-workspace ops stdout contained extra content.");
    ValidateSafeStderr(completion.Stderr, workspaceRoot, "bookstudio.db");
}

static Task CallToolAsync(
    OpsChildProcess process,
    int id,
    string name) =>
    process.SendRequestAsync(
        id,
        "tools/call",
        new
        {
            name,
            arguments = new { },
        });

static async Task InitializeAndReadyAsync(
    OpsChildProcess process,
    int id,
    string clientName)
{
    await process.SendRequestAsync(
        id,
        "initialize",
        new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = clientName, version = "1.0.0" },
        });
    using (var response = await process.ReadJsonAsync())
    {
        Require(response.RootElement.GetProperty("id").GetInt32() == id, "Initialize response ID mismatch.");
    }
    await process.SendNotificationAsync("notifications/initialized", new { });
}

static void AssertCapabilityParity(
    string capabilityResource,
    JsonElement diagnosticsCapabilities)
{
    using var resource = JsonDocument.Parse(capabilityResource);
    var expected = resource.RootElement.GetProperty("capabilities")
        .EnumerateArray()
        .Select(capability =>
            capability.GetProperty("id").GetString() + "|" +
            capability.GetProperty("status").GetString() + "|" +
            capability.GetProperty("phase").GetString())
        .ToArray();
    var actual = diagnosticsCapabilities
        .EnumerateArray()
        .Select(capability =>
            capability.GetProperty("id").GetString() + "|" +
            capability.GetProperty("status").GetString() + "|" +
            capability.GetProperty("phase").GetString())
        .ToArray();
    Require(expected.SequenceEqual(actual), "Capability resource and diagnostics are not identical.");
}

static void AssertJsonRpcError(JsonElement response, int code)
{
    Require(
        response.GetProperty("error").GetProperty("code").GetInt32() == code,
        $"Expected JSON-RPC error {code}.");
}

static void AssertNoLeaks(string json, string workspaceRoot)
{
    Require(
        !json.Contains(workspaceRoot, StringComparison.OrdinalIgnoreCase),
        "Ops response leaked the workspace path.");
    Require(
        !json.Contains("bookstudio.db", StringComparison.OrdinalIgnoreCase),
        "Ops response leaked the database file name.");
    Require(
        !json.Contains("Data Source", StringComparison.OrdinalIgnoreCase),
        "Ops response leaked a connection string.");
    Require(
        !json.Contains("stackTrace", StringComparison.OrdinalIgnoreCase),
        "Ops response leaked a stack trace.");
}

static string[] Inventory(string workspaceRoot) =>
    Directory.Exists(workspaceRoot)
        ? Directory.EnumerateFiles(workspaceRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(workspaceRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray()
        : [];

static void ValidateSafeStderr(string stderr, params string[] forbiddenValues)
{
    foreach (var forbidden in forbiddenValues)
    {
        Require(
            !stderr.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            "stderr leaked a forbidden value.");
    }
    foreach (var line in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
    {
        Require(line.Length <= 96, "stderr diagnostic exceeded its bound.");
        Require(
            line.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '_' or '-'),
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

static void TryDelete(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

internal sealed class OpsChildProcess : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly Process _process;
    private bool _inputClosed;

    private OpsChildProcess(Process process) => _process = process;

    public static OpsChildProcess Start(string workspaceRoot)
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "BookStudio.Mcp.Ops.dll");
        if (!File.Exists(assembly))
        {
            throw new FileNotFoundException(
                "BookStudio.Mcp.Ops.dll is missing from integration output.",
                assembly);
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
        startInfo.ArgumentList.Add(assembly);
        startInfo.ArgumentList.Add("--workspace-root");
        startInfo.ArgumentList.Add(workspaceRoot);
        return new OpsChildProcess(
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("The ops MCP child process could not be started."));
    }

    public Task SendRequestAsync(
        int id,
        string method,
        object parameters) =>
        SendObjectAsync(new { jsonrpc = "2.0", id, method, @params = parameters });

    public Task SendNotificationAsync(
        string method,
        object parameters) =>
        SendObjectAsync(new { jsonrpc = "2.0", method, @params = parameters });

    private async Task SendObjectAsync(object message)
    {
        if (_inputClosed)
        {
            throw new InvalidOperationException("MCP stdin is closed.");
        }
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message))
            .ConfigureAwait(false);
        await _process.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    public async Task<JsonDocument> ReadJsonAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var line = await _process.StandardOutput.ReadLineAsync(timeout.Token)
            .ConfigureAwait(false);
        if (line is null)
        {
            var stderr = await _process.StandardError.ReadToEndAsync(timeout.Token)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "Ops MCP process ended before a response. stderr=" + stderr);
        }
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Ops stdout contained non-JSON MCP content.",
                exception);
        }
    }

    public async Task<OpsCompletion> CloseInputAndWaitAsync()
    {
        if (!_inputClosed)
        {
            _inputClosed = true;
            _process.StandardInput.Close();
        }
        using var timeout = new CancellationTokenSource(Timeout);
        await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        var stdout = await _process.StandardOutput.ReadToEndAsync(timeout.Token)
            .ConfigureAwait(false);
        var stderr = await _process.StandardError.ReadToEndAsync(timeout.Token)
            .ConfigureAwait(false);
        return new OpsCompletion(_process.ExitCode, stdout, stderr);
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

internal sealed record OpsCompletion(
    int ExitCode,
    string RemainingStdout,
    string Stderr);
