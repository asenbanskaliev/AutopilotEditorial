using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Artifacts;
using BookStudio.Infrastructure.Artifacts.FileSystem;

var root = Path.Combine(
    Path.GetTempPath(),
    "BookStudio.Tests.BookCore",
    Guid.NewGuid().ToString("N"));

try
{
    await VerifyLazyWorkspaceAsync(Path.Combine(root, "lazy-workspace"));
    var workspace = Path.Combine(root, "artifact-workspace");
    var binary = new byte[] { 0, 1, 2, 3, 250, 251, 252, 253 };
    await SeedArtifactsAsync(workspace, binary);
    await VerifyBookCoreJourneyAsync(workspace, binary);
    Console.WriteLine(
        "book-core integration PASS: capabilities, schemas, tools, resources, artifact reads, diff, confinement, bounds and EOF verified.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("book-core integration FAIL: " + exception);
    return 1;
}
finally
{
    TryDelete(root);
}

static async Task VerifyLazyWorkspaceAsync(string workspaceRoot)
{
    Require(!Directory.Exists(workspaceRoot), "Lazy workspace unexpectedly exists before process start.");
    await using var server = McpChildProcess.Start(workspaceRoot);
    await InitializeAndReadyAsync(server, 1);
    await server.SendRequestAsync(2, "tools/list", new { });
    using (var response = await server.ReadJsonAsync())
    {
        Require(response.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() == 2, "Active tool count mismatch.");
    }

    var completion = await server.CloseInputAndWaitAsync();
    Require(completion.ExitCode == 0, "Lazy-workspace process did not exit cleanly.");
    Require(string.IsNullOrWhiteSpace(completion.RemainingStdout), "Lazy-workspace stdout contained unexpected content.");
    Require(!Directory.Exists(workspaceRoot), "Initialize and list operations created the lazy workspace.");
    ValidateSafeStderr(completion.Stderr);
}

static async Task SeedArtifactsAsync(string workspaceRoot, byte[] binary)
{
    Directory.CreateDirectory(workspaceRoot);
    await using var store = new FileArtifactStore(
        FileArtifactStoreOptions.Create(workspaceRoot));

    await PutTextAsync(
        store,
        "demo.chapter-01",
        1,
        "alpha\nbeta\ngamma\n");
    await PutTextAsync(
        store,
        "demo.chapter-01",
        2,
        "alpha\nbeta revised\ngamma\ndelta\n");

    await using (var binaryStream = new MemoryStream(binary, writable: false))
    {
        _ = await store.PutAsync(new ArtifactWriteRequest(
            "demo.cover",
            1,
            "application/octet-stream",
            binaryStream));
    }

    var large = new byte[ArtifactQueryService.MaximumResourceBytes + 1];
    Array.Fill(large, (byte)'x');
    await using (var largeStream = new MemoryStream(large, writable: false))
    {
        _ = await store.PutAsync(new ArtifactWriteRequest(
            "demo.large",
            1,
            "text/plain",
            largeStream));
    }
}

static async Task PutTextAsync(
    IArtifactStore store,
    string artifactId,
    int version,
    string content)
{
    await using var stream = new MemoryStream(
        Encoding.UTF8.GetBytes(content),
        writable: false);
    _ = await store.PutAsync(new ArtifactWriteRequest(
        artifactId,
        version,
        "text/markdown; charset=utf-8",
        stream));
}

static async Task VerifyBookCoreJourneyAsync(
    string workspaceRoot,
    byte[] binary)
{
    await using var server = McpChildProcess.Start(workspaceRoot);
    await server.SendRequestAsync(
        1,
        "initialize",
        new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "book-core-integration", version = "1.0.0" },
        });

    using (var initialized = await server.ReadJsonAsync())
    {
        var result = initialized.RootElement.GetProperty("result");
        var capabilities = result.GetProperty("capabilities");
        Require(capabilities.EnumerateObject().Count() == 3, "Initialize advertised unexpected capabilities.");
        var tools = capabilities.GetProperty("tools");
        Require(!tools.GetProperty("listChanged").GetBoolean(), "tools.listChanged must be false.");
        var resources = capabilities.GetProperty("resources");
        Require(!resources.GetProperty("subscribe").GetBoolean(), "resources.subscribe must be false.");
        Require(!resources.GetProperty("listChanged").GetBoolean(), "resources.listChanged must be false.");
        var prompts = capabilities.GetProperty("prompts");
        Require(!prompts.GetProperty("listChanged").GetBoolean(), "prompts.listChanged must be false.");
        foreach (var forbidden in new[]
                 {
                     "logging",
                     "completions",
                     "sampling",
                     "roots",
                     "tasks",
                     "experimental",
                 })
        {
            Require(!capabilities.TryGetProperty(forbidden, out _), $"Unexpected capability advertised: {forbidden}");
        }
        Require(
            result.GetProperty("instructions").GetString()?.Contains("artifact", StringComparison.OrdinalIgnoreCase) == true,
            "Initialize instructions do not describe the active surface.");
    }

    await server.SendNotificationAsync("notifications/initialized", new { });

    await server.SendRequestAsync(2, "tools/list", new { });
    string[] activeToolNames;
    using (var toolList = await server.ReadJsonAsync())
    {
        var tools = toolList.RootElement.GetProperty("result").GetProperty("tools");
        activeToolNames = tools.EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString() ?? string.Empty)
            .ToArray();
        Require(
            activeToolNames.SequenceEqual(
                new[] { "book.artifact.compare", "book.artifact.get" },
                StringComparer.Ordinal),
            "Active tools are not deterministic or complete.");
        Require(!activeToolNames.Any(name => name.Contains("project", StringComparison.Ordinal)), "Reserved project tool was advertised.");
        Require(!activeToolNames.Any(name => name.Contains("decision", StringComparison.Ordinal)), "Reserved decision tool was advertised.");

        foreach (var tool in tools.EnumerateArray())
        {
            Require(tool.GetProperty("inputSchema").GetProperty("type").GetString() == "object", "inputSchema is missing.");
            Require(tool.GetProperty("outputSchema").GetProperty("type").GetString() == "object", "outputSchema is missing.");
            var annotations = tool.GetProperty("annotations");
            Require(annotations.GetProperty("readOnlyHint").GetBoolean(), "Tool must be read-only.");
            Require(!annotations.GetProperty("destructiveHint").GetBoolean(), "Tool cannot be destructive.");
            Require(annotations.GetProperty("idempotentHint").GetBoolean(), "Tool must be idempotent.");
            Require(!annotations.GetProperty("openWorldHint").GetBoolean(), "Tool cannot be open-world.");
            Require(tool.GetProperty("execution").GetProperty("taskSupport").GetString() == "forbidden", "Task support mismatch.");
        }
    }

    await server.SendRequestAsync(3, "resources/list", new { });
    string cursor;
    var resourceUris = new List<string>();
    using (var firstResources = await server.ReadJsonAsync())
    {
        var result = firstResources.RootElement.GetProperty("result");
        resourceUris.AddRange(result.GetProperty("resources").EnumerateArray()
            .Select(item => item.GetProperty("uri").GetString() ?? string.Empty));
        cursor = result.GetProperty("nextCursor").GetString()
            ?? throw new InvalidOperationException("First resource page did not provide a cursor.");
    }

    await server.SendRequestAsync(4, "resources/list", new { cursor });
    using (var secondResources = await server.ReadJsonAsync())
    {
        var result = secondResources.RootElement.GetProperty("result");
        resourceUris.AddRange(result.GetProperty("resources").EnumerateArray()
            .Select(item => item.GetProperty("uri").GetString() ?? string.Empty));
        cursor = result.GetProperty("nextCursor").GetString()
            ?? throw new InvalidOperationException("Second resource page did not provide a cursor.");
    }

    await server.SendRequestAsync(100, "resources/list", new { cursor });
    using (var thirdResources = await server.ReadJsonAsync())
    {
        var result = thirdResources.RootElement.GetProperty("result");
        var resources = result.GetProperty("resources").EnumerateArray().ToArray();
        Require(resources.Length == 1, "Third resource page size mismatch.");
        resourceUris.AddRange(resources.Select(item => item.GetProperty("uri").GetString() ?? string.Empty));
        Require(!result.TryGetProperty("nextCursor", out _), "Last resource page must not have a cursor.");
    }
    Require(resourceUris.Count == 7, "Merged resource count mismatch.");
    Require(resourceUris.Count(uri => uri.StartsWith("book://schemas/book-core/", StringComparison.Ordinal)) == 6, "Schema resource count mismatch.");
    Require(resourceUris.Contains("book://prompts/book-core/inspect-artifact/v1"), "book-core prompt resource is missing.");
    Require(resourceUris.SequenceEqual(resourceUris.OrderBy(value => value, StringComparer.Ordinal)), "Resources are not ordinally sorted.");

    await server.SendRequestAsync(5, "resources/list", new { cursor = cursor + "x" });
    using (var invalidCursor = await server.ReadJsonAsync())
    {
        AssertJsonRpcError(invalidCursor.RootElement, -32602);
    }

    await server.SendRequestAsync(6, "resources/templates/list", new { });
    using (var templates = await server.ReadJsonAsync())
    {
        var template = templates.RootElement.GetProperty("result").GetProperty("resourceTemplates")[0];
        Require(
            template.GetProperty("uriTemplate").GetString() ==
            "book://project/{projectId}/artifact/{artifactId}/versions/{version}",
            "Artifact resource template mismatch.");
    }

    await server.SendRequestAsync(
        7,
        "resources/read",
        new { uri = "book://schemas/book-core/artifact-get-input" });
    using (var schema = await server.ReadJsonAsync())
    {
        var content = schema.RootElement.GetProperty("result").GetProperty("contents")[0];
        Require(content.GetProperty("mimeType").GetString() == "application/schema+json", "Schema MIME type mismatch.");
        using var schemaJson = JsonDocument.Parse(content.GetProperty("text").GetString()!);
        Require(schemaJson.RootElement.GetProperty("type").GetString() == "object", "Schema resource content is invalid.");
    }

    await server.SendRequestAsync(
        8,
        "tools/call",
        new
        {
            name = "book.artifact.get",
            arguments = new
            {
                projectId = "demo",
                payload = new
                {
                    artifactId = "demo.chapter-01",
                    version = 1,
                    includeContent = true,
                },
            },
        });
    string textResourceUri;
    using (var get = await server.ReadJsonAsync())
    {
        var raw = get.RootElement.GetRawText();
        AssertNoPhysicalPath(raw, workspaceRoot);
        var result = get.RootElement.GetProperty("result");
        Require(!result.GetProperty("isError").GetBoolean(), "artifact.get unexpectedly failed.");
        var structured = result.GetProperty("structuredContent");
        Require(structured.GetProperty("resultType").GetString() == "complete", "artifact.get resultType mismatch.");
        var data = structured.GetProperty("data");
        Require(data.GetProperty("contentIncluded").GetBoolean(), "Text content was not included.");
        Require(data.GetProperty("inlineText").GetString()?.Contains("beta", StringComparison.Ordinal) == true, "Inline text mismatch.");
        var link = result.GetProperty("content").EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == "resource_link");
        textResourceUri = link.GetProperty("uri").GetString()
            ?? throw new InvalidOperationException("Artifact resource URI is missing.");
    }

    await server.SendRequestAsync(9, "resources/read", new { uri = textResourceUri });
    using (var resource = await server.ReadJsonAsync())
    {
        var content = resource.RootElement.GetProperty("result").GetProperty("contents")[0];
        Require(content.GetProperty("text").GetString() == "alpha\nbeta\ngamma\n", "Text resource content mismatch.");
        AssertNoPhysicalPath(resource.RootElement.GetRawText(), workspaceRoot);
    }

    await server.SendRequestAsync(
        10,
        "tools/call",
        new
        {
            name = "book.artifact.compare",
            arguments = new
            {
                projectId = "demo",
                payload = new
                {
                    artifactId = "demo.chapter-01",
                    leftVersion = 1,
                    rightVersion = 2,
                    maxDifferences = 10,
                },
            },
        });
    using (var compare = await server.ReadJsonAsync())
    {
        var result = compare.RootElement.GetProperty("result");
        Require(!result.GetProperty("isError").GetBoolean(), "artifact.compare unexpectedly failed.");
        var data = result.GetProperty("structuredContent").GetProperty("data");
        Require(!data.GetProperty("identical").GetBoolean(), "Different versions were reported identical.");
        var summary = data.GetProperty("summary");
        Require(summary.GetProperty("textDiffPerformed").GetBoolean(), "Text diff was not performed.");
        Require(summary.GetProperty("addedLines").GetInt32() >= 2, "Added-line count is invalid.");
        Require(summary.GetProperty("removedLines").GetInt32() >= 1, "Removed-line count is invalid.");
        Require(data.GetProperty("differences").GetArrayLength() >= 3, "Structured differences are missing.");
        AssertNoPhysicalPath(compare.RootElement.GetRawText(), workspaceRoot);
    }

    await server.SendRequestAsync(
        11,
        "tools/call",
        new
        {
            name = "book.artifact.get",
            arguments = new
            {
                projectId = "other",
                payload = new { artifactId = "demo.chapter-01", version = 1 },
            },
        });
    using (var scopeFailure = await server.ReadJsonAsync())
    {
        var result = scopeFailure.RootElement.GetProperty("result");
        Require(result.GetProperty("isError").GetBoolean(), "Project-scope violation must be a tool error.");
        Require(
            result.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString() ==
            "artifact_scope_violation",
            "Project-scope error code mismatch.");
    }

    await server.SendRequestAsync(
        12,
        "tools/call",
        new { name = "book.project.create", arguments = new { projectId = "demo", payload = new { } } });
    using (var reserved = await server.ReadJsonAsync())
    {
        AssertJsonRpcError(reserved.RootElement, -32602);
    }

    await server.SendRequestAsync(
        13,
        "tools/call",
        new
        {
            name = "book.artifact.get",
            arguments = new
            {
                projectId = "demo",
                payload = new { artifactId = "demo.cover", version = 1, includeContent = true },
            },
        });
    string binaryUri;
    using (var binaryGet = await server.ReadJsonAsync())
    {
        var result = binaryGet.RootElement.GetProperty("result");
        Require(!result.GetProperty("isError").GetBoolean(), "Binary artifact metadata lookup failed.");
        var structured = result.GetProperty("structuredContent");
        Require(!structured.GetProperty("data").GetProperty("contentIncluded").GetBoolean(), "Binary content was incorrectly inlined.");
        Require(structured.GetProperty("warnings").GetArrayLength() == 1, "Binary content warning is missing.");
        binaryUri = result.GetProperty("content").EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == "resource_link")
            .GetProperty("uri")
            .GetString()!;
    }

    await server.SendRequestAsync(14, "resources/read", new { uri = binaryUri });
    using (var binaryRead = await server.ReadJsonAsync())
    {
        var content = binaryRead.RootElement.GetProperty("result").GetProperty("contents")[0];
        Require(content.GetProperty("blob").GetString() == Convert.ToBase64String(binary), "Binary resource blob mismatch.");
        Require(!content.TryGetProperty("text", out _), "Binary resource exposed a text property.");
    }

    await server.SendRequestAsync(
        15,
        "resources/read",
        new { uri = "book://project/demo/artifact/demo.large/versions/1" });
    using (var largeRead = await server.ReadJsonAsync())
    {
        AssertJsonRpcError(largeRead.RootElement, -32602);
        Require(
            largeRead.RootElement.GetProperty("error").GetProperty("data").GetProperty("code").GetString() ==
            "resource_too_large",
            "Oversize resource error code mismatch.");
    }

    await server.SendRequestAsync(16, "resources/read", new { uri = "book://project/demo/unknown" });
    using (var unknownResource = await server.ReadJsonAsync())
    {
        AssertJsonRpcError(unknownResource.RootElement, -32602);
    }

    var completion = await server.CloseInputAndWaitAsync();
    Require(completion.ExitCode == 0, "book-core process did not exit cleanly.");
    Require(string.IsNullOrWhiteSpace(completion.RemainingStdout), "book-core stdout contained unexpected content.");
    ValidateSafeStderr(completion.Stderr, workspaceRoot);
}

static async Task InitializeAndReadyAsync(McpChildProcess server, int id)
{
    await server.SendRequestAsync(
        id,
        "initialize",
        new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "lazy-client", version = "1.0.0" },
        });
    using (var response = await server.ReadJsonAsync())
    {
        Require(response.RootElement.GetProperty("id").GetInt32() == id, "Initialize response ID mismatch.");
    }
    await server.SendNotificationAsync("notifications/initialized", new { });
}

static void AssertJsonRpcError(JsonElement response, int code)
{
    var error = response.GetProperty("error");
    Require(error.GetProperty("code").GetInt32() == code, $"Expected JSON-RPC error {code}.");
}

static void AssertNoPhysicalPath(string json, string workspaceRoot)
{
    Require(!json.Contains(workspaceRoot, StringComparison.OrdinalIgnoreCase), "MCP response leaked the workspace path.");
    Require(!json.Contains(".bookstudio", StringComparison.OrdinalIgnoreCase), "MCP response leaked an artifact-store path.");
}

static void ValidateSafeStderr(string stderr, params string[] forbiddenValues)
{
    foreach (var forbidden in forbiddenValues)
    {
        Require(!stderr.Contains(forbidden, StringComparison.OrdinalIgnoreCase), "stderr leaked a forbidden value.");
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

static void TryDelete(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch (IOException)
    {
        // Best-effort cleanup after all stores and processes have been disposed.
    }
    catch (UnauthorizedAccessException)
    {
        // Best-effort cleanup after all stores and processes have been disposed.
    }
}

internal sealed class McpChildProcess : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly Process _process;
    private bool _inputClosed;

    private McpChildProcess(Process process)
    {
        _process = process;
    }

    public static McpChildProcess Start(string workspaceRoot)
    {
        var mcpAssembly = Path.Combine(AppContext.BaseDirectory, "BookStudio.Mcp.dll");
        if (!File.Exists(mcpAssembly))
        {
            throw new FileNotFoundException("BookStudio.Mcp.dll is missing from integration output.", mcpAssembly);
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
        startInfo.ArgumentList.Add("--workspace-root");
        startInfo.ArgumentList.Add(workspaceRoot);

        return new McpChildProcess(
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("The book-core MCP child process could not be started."));
    }

    public Task SendRequestAsync(int id, string method, object parameters) =>
        SendObjectAsync(new { jsonrpc = "2.0", id, method, @params = parameters });

    public Task SendNotificationAsync(string method, object parameters) =>
        SendObjectAsync(new { jsonrpc = "2.0", method, @params = parameters });

    private async Task SendObjectAsync(object message)
    {
        if (_inputClosed)
        {
            throw new InvalidOperationException("MCP stdin is closed.");
        }
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message)).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    public async Task<JsonDocument> ReadJsonAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var line = await _process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
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
        var stdout = await _process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        var stderr = await _process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        return new McpCompletion(_process.ExitCode, stdout, stderr);
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

internal sealed record McpCompletion(int ExitCode, string RemainingStdout, string Stderr);
