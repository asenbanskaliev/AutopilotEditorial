using System.Diagnostics;
using System.Text;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "bookstudio-authoring-" + Guid.NewGuid().ToString("N"));
var lazyRoot = root + "-lazy";
try
{
    await VerifyLazyWorkspaceAsync(lazyRoot);
    await VerifyAuthoringJourneyAsync(root);
    Console.WriteLine("BOOK_AUTHORING_INTEGRATION_PASS");
    return 0;
}
finally
{
    TryDelete(root);
    TryDelete(lazyRoot);
}

static async Task VerifyLazyWorkspaceAsync(string workspaceRoot)
{
    Require(!Directory.Exists(workspaceRoot), "Lazy workspace unexpectedly exists before process launch.");
    await using var server = AuthoringChildProcess.Start(workspaceRoot);
    await InitializeAndReadyAsync(server, 1);
    await server.SendRequestAsync(2, "tools/list", new { });
    using (var tools = await server.ReadJsonAsync())
    {
        Require(tools.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() == 2, "Authoring tool count mismatch.");
    }
    Require(!Directory.Exists(workspaceRoot), "Initialize or tools/list created the authoring workspace.");
    var completion = await server.CloseInputAndWaitAsync();
    Require(completion.ExitCode == 0, "Lazy authoring process did not exit cleanly.");
    Require(string.IsNullOrWhiteSpace(completion.RemainingStdout), "Lazy authoring stdout contained extra content.");
    ValidateSafeStderr(completion.Stderr, workspaceRoot);
}

static async Task VerifyAuthoringJourneyAsync(string workspaceRoot)
{
    await using var server = AuthoringChildProcess.Start(workspaceRoot);
    await server.SendRequestAsync(
        1,
        "initialize",
        new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "authoring-integration", version = "1.0.0" },
        });
    using (var initialize = await server.ReadJsonAsync())
    {
        var result = initialize.RootElement.GetProperty("result");
        var serverInfo = result.GetProperty("serverInfo");
        Require(serverInfo.GetProperty("name").GetString() == "bookstudio-authoring", "Authoring server name mismatch.");
        Require(serverInfo.GetProperty("title").GetString() == "BookStudio Authoring MCP", "Authoring server title mismatch.");
        var capabilities = result.GetProperty("capabilities");
        Require(
            capabilities.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[] { "prompts", "resources", "tools" }),
            "Authoring capabilities are not exact.");
        Require(!capabilities.GetProperty("prompts").GetProperty("listChanged").GetBoolean(), "Authoring prompts.listChanged must be false.");
    }
    await server.SendNotificationAsync("notifications/initialized", new { });

    await server.SendRequestAsync(2, "tools/list", new { });
    using (var toolsResponse = await server.ReadJsonAsync())
    {
        var tools = toolsResponse.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Require(tools.Length == 2, "Authoring tools/list must expose exactly two tools.");
        var names = tools.Select(tool => tool.GetProperty("name").GetString()).ToArray();
        Require(names.SequenceEqual(new[] { "book.draft.register", "book.draft.validate" }), "Authoring tool ordering mismatch.");
        Require(!toolsResponse.RootElement.GetRawText().Contains("book.scene.generate", StringComparison.Ordinal), "Reserved tool was advertised.");
        var register = tools.Single(tool => tool.GetProperty("name").GetString() == "book.draft.register");
        var validate = tools.Single(tool => tool.GetProperty("name").GetString() == "book.draft.validate");
        Require(!register.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), "Register tool readOnly annotation mismatch.");
        Require(!register.GetProperty("annotations").GetProperty("idempotentHint").GetBoolean(), "Register tool idempotent annotation mismatch.");
        Require(validate.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), "Validate tool readOnly annotation mismatch.");
        Require(validate.GetProperty("annotations").GetProperty("idempotentHint").GetBoolean(), "Validate tool idempotent annotation mismatch.");
    }

    var resourceUris = new List<string>();
    await server.SendRequestAsync(3, "resources/list", new { });
    string cursor;
    using (var resourcesPage1 = await server.ReadJsonAsync())
    {
        var result = resourcesPage1.RootElement.GetProperty("result");
        var resources = result.GetProperty("resources");
        Require(resources.GetArrayLength() == 3, "Authoring resources first page size mismatch.");
        resourceUris.AddRange(resources.EnumerateArray().Select(item => item.GetProperty("uri").GetString()!));
        cursor = result.GetProperty("nextCursor").GetString()!;
    }
    await server.SendRequestAsync(4, "resources/list", new { cursor });
    using (var resourcesPage2 = await server.ReadJsonAsync())
    {
        var result = resourcesPage2.RootElement.GetProperty("result");
        var resources = result.GetProperty("resources");
        Require(resources.GetArrayLength() == 3, "Authoring resources second page size mismatch.");
        resourceUris.AddRange(resources.EnumerateArray().Select(item => item.GetProperty("uri").GetString()!));
        cursor = result.GetProperty("nextCursor").GetString()!;
    }
    await server.SendRequestAsync(100, "resources/list", new { cursor });
    using (var resourcesPage3 = await server.ReadJsonAsync())
    {
        var result = resourcesPage3.RootElement.GetProperty("result");
        var resources = result.GetProperty("resources");
        Require(resources.GetArrayLength() == 1, "Authoring resources third page size mismatch.");
        resourceUris.AddRange(resources.EnumerateArray().Select(item => item.GetProperty("uri").GetString()!));
        Require(!result.TryGetProperty("nextCursor", out _), "Authoring resources returned an unexpected fourth page.");
    }
    Require(resourceUris.Count == 7, "Authoring merged resource count mismatch.");
    Require(resourceUris.SequenceEqual(resourceUris.OrderBy(uri => uri, StringComparer.Ordinal)), "Authoring resources are not ordinally sorted.");
    Require(resourceUris.Contains("book://prompts/book-authoring/validate-draft/v1"), "Authoring prompt resource is missing.");
    var schemaUri = resourceUris.First(uri => uri.StartsWith("book://schemas/book-authoring/", StringComparison.Ordinal));

    await server.SendRequestAsync(5, "resources/read", new { uri = schemaUri });
    using (var schema = await server.ReadJsonAsync())
    {
        var content = schema.RootElement.GetProperty("result").GetProperty("contents")[0];
        Require(content.GetProperty("mimeType").GetString() == "application/schema+json", "Authoring schema media type mismatch.");
    }

    const string artifactId = "demo.draft.chapter-01";
    const string cleanContent = "# Chapter One\n\nA clean opening paragraph.\n";
    await server.SendRequestAsync(
        6,
        "tools/call",
        new
        {
            name = "book.draft.register",
            arguments = new
            {
                projectId = "demo",
                payload = new
                {
                    artifactId,
                    expectedVersion = 1,
                    mediaType = "text/markdown",
                    content = cleanContent,
                },
            },
        });
    string draftUri;
    using (var registered = await server.ReadJsonAsync())
    {
        var result = registered.RootElement.GetProperty("result");
        Require(!result.GetProperty("isError").GetBoolean(), "Draft registration failed.");
        var structured = result.GetProperty("structuredContent");
        Require(structured.GetProperty("resultType").GetString() == "complete", "Draft registration resultType mismatch.");
        draftUri = result.GetProperty("content").EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == "resource_link")
            .GetProperty("uri").GetString()!;
        AssertNoPhysicalPath(registered.RootElement.GetRawText(), workspaceRoot);
    }
    Require(Directory.Exists(workspaceRoot), "Draft registration did not create the workspace.");

    await server.SendRequestAsync(
        7,
        "tools/call",
        new
        {
            name = "book.draft.validate",
            arguments = new
            {
                projectId = "demo",
                payload = new { artifactId, version = 1, maximumLineLength = 120 },
            },
        });
    using (var validated = await server.ReadJsonAsync())
    {
        var result = validated.RootElement.GetProperty("result");
        Require(!result.GetProperty("isError").GetBoolean(), "Clean draft validation failed.");
        var structured = result.GetProperty("structuredContent");
        Require(structured.GetProperty("warnings").GetArrayLength() == 0, "Clean draft unexpectedly produced warnings.");
        var metrics = structured.GetProperty("data").GetProperty("metrics");
        Require(metrics.GetProperty("markdownHeadings").GetInt32() == 1, "Markdown heading metric mismatch.");
        Require(metrics.GetProperty("words").GetInt32() >= 6, "Word metric is unexpectedly low.");
    }

    await server.SendRequestAsync(8, "resources/read", new { uri = draftUri });
    using (var resource = await server.ReadJsonAsync())
    {
        var content = resource.RootElement.GetProperty("result").GetProperty("contents")[0];
        Require(content.GetProperty("text").GetString() == cleanContent, "Draft resource text mismatch.");
        Require(!content.TryGetProperty("blob", out _), "Authoring resource exposed a binary blob.");
        AssertNoPhysicalPath(resource.RootElement.GetRawText(), workspaceRoot);
    }

    await SendRegisterAsync(server, 9, "demo", artifactId, 1, cleanContent);
    using (var conflict = await server.ReadJsonAsync())
    {
        AssertToolError(conflict.RootElement, "draft_version_conflict");
    }

    await SendRegisterAsync(server, 10, "other", artifactId, 1, cleanContent);
    using (var scope = await server.ReadJsonAsync())
    {
        AssertToolError(scope.RootElement, "draft_scope_violation");
    }

    await SendRegisterAsync(server, 11, "demo", "demo.draft.invalid-control", 1, "bad\0content");
    using (var invalidControl = await server.ReadJsonAsync())
    {
        AssertToolError(invalidControl.RootElement, "invalid_draft_controls");
    }

    var warningContent = "# Chapter One\n\n" + new string('x', 130) + " \nA\tline with a tab.\n";
    await SendRegisterAsync(server, 12, "demo", artifactId, 2, warningContent);
    using (var registeredV2 = await server.ReadJsonAsync())
    {
        Require(!registeredV2.RootElement.GetProperty("result").GetProperty("isError").GetBoolean(), "Draft version 2 registration failed.");
    }

    await server.SendRequestAsync(
        13,
        "tools/call",
        new
        {
            name = "book.draft.validate",
            arguments = new
            {
                projectId = "demo",
                payload = new { artifactId, version = 2, maximumLineLength = 100 },
            },
        });
    using (var warningValidation = await server.ReadJsonAsync())
    {
        var warnings = warningValidation.RootElement.GetProperty("result")
            .GetProperty("structuredContent")
            .GetProperty("warnings")
            .EnumerateArray()
            .Select(item => item.GetProperty("code").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Require(warnings.Contains("line_too_long"), "Long-line warning missing.");
        Require(warnings.Contains("trailing_whitespace"), "Trailing-whitespace warning missing.");
        Require(warnings.Contains("tab_character"), "Tab warning missing.");
    }

    await server.SendRequestAsync(
        14,
        "tools/call",
        new
        {
            name = "book.scene.generate",
            arguments = new { projectId = "demo", payload = new { } },
        });
    using (var reserved = await server.ReadJsonAsync())
    {
        AssertJsonRpcError(reserved.RootElement, -32602);
    }

    var completion = await server.CloseInputAndWaitAsync();
    Require(completion.ExitCode == 0, "Authoring MCP process did not exit cleanly.");
    Require(string.IsNullOrWhiteSpace(completion.RemainingStdout), "Authoring MCP stdout contained unexpected content.");
    ValidateSafeStderr(completion.Stderr, workspaceRoot, cleanContent, warningContent);
}

static Task SendRegisterAsync(
    AuthoringChildProcess server,
    int id,
    string projectId,
    string artifactId,
    int version,
    string content) =>
    server.SendRequestAsync(
        id,
        "tools/call",
        new
        {
            name = "book.draft.register",
            arguments = new
            {
                projectId,
                payload = new
                {
                    artifactId,
                    expectedVersion = version,
                    mediaType = "text/markdown",
                    content,
                },
            },
        });

static async Task InitializeAndReadyAsync(AuthoringChildProcess server, int id)
{
    await server.SendRequestAsync(
        id,
        "initialize",
        new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "lazy-authoring-client", version = "1.0.0" },
        });
    using (var response = await server.ReadJsonAsync())
    {
        Require(response.RootElement.GetProperty("id").GetInt32() == id, "Initialize response ID mismatch.");
    }
    await server.SendNotificationAsync("notifications/initialized", new { });
}

static void AssertToolError(JsonElement response, string code)
{
    var result = response.GetProperty("result");
    Require(result.GetProperty("isError").GetBoolean(), "Expected a tool execution error.");
    Require(result.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString() == code, $"Expected tool error {code}.");
}

static void AssertJsonRpcError(JsonElement response, int code)
{
    Require(response.GetProperty("error").GetProperty("code").GetInt32() == code, $"Expected JSON-RPC error {code}.");
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
        Require(line.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'), "stderr contained non-diagnostic content.");
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

internal sealed class AuthoringChildProcess : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly Process _process;
    private bool _inputClosed;

    private AuthoringChildProcess(Process process) => _process = process;

    public static AuthoringChildProcess Start(string workspaceRoot)
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "BookStudio.Mcp.Authoring.dll");
        if (!File.Exists(assembly))
        {
            throw new FileNotFoundException("BookStudio.Mcp.Authoring.dll is missing from integration output.", assembly);
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
        return new AuthoringChildProcess(
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("The authoring MCP child process could not be started."));
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

    public async Task<AuthoringCompletion> CloseInputAndWaitAsync()
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
        return new AuthoringCompletion(_process.ExitCode, stdout, stderr);
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

internal sealed record AuthoringCompletion(int ExitCode, string RemainingStdout, string Stderr);
