using System.Diagnostics;
using System.Text;
using System.Text.Json;

var workspaceRoot = Path.Combine(Path.GetTempPath(), "bookstudio-quality-" + Guid.NewGuid().ToString("N"));
var lazyRoot = workspaceRoot + "-lazy";
const string cleanArtifact = "demo.draft.clean";
const string failingArtifact = "demo.draft.failing";
const string cleanContent = "# Clean Draft\n\nA clean-opening-secret paragraph contains enough distinct words to pass the deterministic minimum word requirement without placeholders or duplicated paragraphs.\n\nA second concise sentence completes the draft safely.\n";
var longSentence = string.Join(' ', Enumerable.Repeat("extendedword", 70)) + ".";
var failingContent = "# Failing Draft\n\nTODO replace this quality-placeholder-secret.\n\nRepeated paragraph.\n\nRepeated paragraph.\n\n" + longSentence + "\n";

try
{
    await VerifyLazyQualityWorkspaceAsync(lazyRoot);
    await RegisterDraftsThroughAuthoringAsync(
        workspaceRoot,
        cleanArtifact,
        cleanContent,
        failingArtifact,
        failingContent);
    var inventoryBefore = Inventory(workspaceRoot);
    await VerifyQualityJourneyAsync(
        workspaceRoot,
        cleanArtifact,
        cleanContent,
        failingArtifact,
        failingContent);
    var inventoryAfter = Inventory(workspaceRoot);
    Require(inventoryBefore.SequenceEqual(inventoryAfter), "Quality process mutated the shared workspace.");
    Console.WriteLine("BOOK_QUALITY_INTEGRATION_PASS");
    return 0;
}
finally
{
    TryDelete(workspaceRoot);
    TryDelete(lazyRoot);
}

static async Task VerifyLazyQualityWorkspaceAsync(string workspaceRoot)
{
    Require(!Directory.Exists(workspaceRoot), "Lazy quality workspace already exists.");
    await using var quality = McpChildProcess.Start("BookStudio.Mcp.Quality.dll", workspaceRoot);
    await InitializeAndReadyAsync(quality, 1, "lazy-quality-client");
    await quality.SendRequestAsync(2, "tools/list", new { });
    using (var response = await quality.ReadJsonAsync())
    {
        Require(response.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() == 2, "Quality tool count mismatch.");
    }
    await quality.SendRequestAsync(3, "resources/list", new { });
    using (var response = await quality.ReadJsonAsync())
    {
        Require(response.RootElement.GetProperty("result").GetProperty("resources").GetArrayLength() == 4, "Quality resource page size mismatch.");
    }
    Require(!Directory.Exists(workspaceRoot), "Quality initialize or listings created the workspace.");
    var completion = await quality.CloseInputAndWaitAsync();
    Require(completion.ExitCode == 0, "Lazy quality process did not exit cleanly.");
    Require(string.IsNullOrWhiteSpace(completion.RemainingStdout), "Lazy quality stdout contained extra content.");
    ValidateSafeStderr(completion.Stderr, workspaceRoot);
}

static async Task RegisterDraftsThroughAuthoringAsync(
    string workspaceRoot,
    string cleanArtifact,
    string cleanContent,
    string failingArtifact,
    string failingContent)
{
    await using var authoring = McpChildProcess.Start("BookStudio.Mcp.Authoring.dll", workspaceRoot);
    await InitializeAndReadyAsync(authoring, 1, "quality-authoring-client");
    await SendRegisterAsync(authoring, 2, cleanArtifact, cleanContent);
    using (var clean = await authoring.ReadJsonAsync())
    {
        Require(!clean.RootElement.GetProperty("result").GetProperty("isError").GetBoolean(), "Clean draft registration failed.");
    }
    await SendRegisterAsync(authoring, 3, failingArtifact, failingContent);
    using (var failing = await authoring.ReadJsonAsync())
    {
        Require(!failing.RootElement.GetProperty("result").GetProperty("isError").GetBoolean(), "Failing fixture draft registration failed.");
    }
    var completion = await authoring.CloseInputAndWaitAsync();
    Require(completion.ExitCode == 0, "Authoring fixture process did not exit cleanly.");
    Require(string.IsNullOrWhiteSpace(completion.RemainingStdout), "Authoring fixture stdout contained extra content.");
    ValidateSafeStderr(completion.Stderr, workspaceRoot, cleanContent, failingContent);
}

static async Task VerifyQualityJourneyAsync(
    string workspaceRoot,
    string cleanArtifact,
    string cleanContent,
    string failingArtifact,
    string failingContent)
{
    await using var quality = McpChildProcess.Start("BookStudio.Mcp.Quality.dll", workspaceRoot);
    await quality.SendRequestAsync(
        1,
        "initialize",
        new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "quality-integration", version = "1.0.0" },
        });
    using (var initialize = await quality.ReadJsonAsync())
    {
        var result = initialize.RootElement.GetProperty("result");
        var info = result.GetProperty("serverInfo");
        Require(info.GetProperty("name").GetString() == "bookstudio-quality", "Quality server name mismatch.");
        Require(info.GetProperty("title").GetString() == "BookStudio Quality MCP", "Quality server title mismatch.");
        var capabilities = result.GetProperty("capabilities");
        var names = capabilities.EnumerateObject().Select(property => property.Name).Order().ToArray();
        Require(names.SequenceEqual(new[] { "prompts", "resources", "tools" }), "Quality capabilities are not exact.");
        Require(!capabilities.GetProperty("prompts").GetProperty("listChanged").GetBoolean(), "Quality prompts.listChanged must be false.");
    }
    await quality.SendNotificationAsync("notifications/initialized", new { });

    await quality.SendRequestAsync(2, "tools/list", new { });
    using (var response = await quality.ReadJsonAsync())
    {
        var tools = response.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Require(tools.Length == 2, "Quality tools/list must expose exactly two tools.");
        var names = tools.Select(tool => tool.GetProperty("name").GetString()).ToArray();
        Require(names.SequenceEqual(new[] { "book.audit.run", "book.gate.evaluate" }), "Quality tool ordering mismatch.");
        foreach (var tool in tools)
        {
            var annotations = tool.GetProperty("annotations");
            Require(annotations.GetProperty("readOnlyHint").GetBoolean(), "Quality tool is not marked read-only.");
            Require(annotations.GetProperty("idempotentHint").GetBoolean(), "Quality tool is not marked idempotent.");
            Require(!annotations.GetProperty("destructiveHint").GetBoolean(), "Quality tool is marked destructive.");
        }
        Require(!response.RootElement.GetRawText().Contains("book.repair.propose", StringComparison.Ordinal), "Reserved quality tool was advertised.");
    }

    var resourceUris = new List<string>();
    await quality.SendRequestAsync(3, "resources/list", new { });
    string cursor;
    using (var page1 = await quality.ReadJsonAsync())
    {
        var result = page1.RootElement.GetProperty("result");
        var resources = result.GetProperty("resources").EnumerateArray().ToArray();
        Require(resources.Length == 4, "Quality resources first page size mismatch.");
        resourceUris.AddRange(resources.Select(resource => resource.GetProperty("uri").GetString()!));
        cursor = result.GetProperty("nextCursor").GetString()!;
    }
    await quality.SendRequestAsync(4, "resources/list", new { cursor });
    using (var page2 = await quality.ReadJsonAsync())
    {
        var result = page2.RootElement.GetProperty("result");
        var resources = result.GetProperty("resources").EnumerateArray().ToArray();
        Require(resources.Length == 4, "Quality resources second page size mismatch.");
        resourceUris.AddRange(resources.Select(resource => resource.GetProperty("uri").GetString()!));
        Require(!result.TryGetProperty("nextCursor", out _), "Quality resources returned an unexpected third page.");
    }
    Require(resourceUris.Count == 8, "Quality merged resource count mismatch.");
    Require(resourceUris.SequenceEqual(resourceUris.OrderBy(uri => uri, StringComparer.Ordinal)), "Quality resources are not ordinally sorted.");
    Require(resourceUris.Contains("book://quality/profiles/draft-basic"), "draft-basic profile is missing.");
    Require(resourceUris.Contains("book://prompts/book-quality/assess-draft/v1"), "Quality prompt resource is missing.");

    await quality.SendRequestAsync(5, "resources/read", new { uri = "book://quality/profiles/draft-basic" });
    using (var profile = await quality.ReadJsonAsync())
    {
        var content = profile.RootElement.GetProperty("result").GetProperty("contents")[0];
        Require(content.GetProperty("mimeType").GetString() == "application/json", "Quality profile media type mismatch.");
        Require(content.GetProperty("text").GetString()!.Contains("content.no_placeholders", StringComparison.Ordinal), "Quality profile check list mismatch.");
    }

    await SendAuditAsync(quality, 6, cleanArtifact, minimumWords: 10, maximumSentenceWords: 60);
    using (var cleanAudit = await quality.ReadJsonAsync())
    {
        var result = cleanAudit.RootElement.GetProperty("result");
        Require(!result.GetProperty("isError").GetBoolean(), "Clean audit returned a tool error.");
        var data = result.GetProperty("structuredContent").GetProperty("data");
        Require(data.GetProperty("isPassing").GetBoolean(), "Clean audit did not pass.");
        Require(data.GetProperty("checks").EnumerateArray().All(check => check.GetProperty("status").GetString() == "pass"), "Clean audit contains non-pass checks.");
        AssertNoLeaks(cleanAudit.RootElement.GetRawText(), workspaceRoot, cleanContent, failingContent);
    }

    await SendGateAsync(quality, 7, cleanArtifact, minimumWords: 10, maximumWarnings: 3, blockOnPlaceholders: true);
    using (var cleanGate = await quality.ReadJsonAsync())
    {
        var data = cleanGate.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data");
        Require(data.GetProperty("decision").GetString() == "PASS", "Clean draft gate did not pass.");
        Require(data.GetProperty("blockingReasons").GetArrayLength() == 0, "Clean gate returned blocking reasons.");
    }

    await SendAuditAsync(quality, 8, failingArtifact, minimumWords: 10, maximumSentenceWords: 20);
    using (var failingAudit = await quality.ReadJsonAsync())
    {
        var data = failingAudit.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data");
        Require(!data.GetProperty("isPassing").GetBoolean(), "Failing audit unexpectedly passed.");
        var checks = data.GetProperty("checks").EnumerateArray()
            .ToDictionary(check => check.GetProperty("id").GetString()!, check => check.GetProperty("status").GetString()!, StringComparer.Ordinal);
        Require(checks["content.no_placeholders"] == "fail", "Placeholder check did not fail.");
        Require(checks["content.no_adjacent_duplicate_paragraphs"] == "warn", "Duplicate paragraph check did not warn.");
        Require(checks["style.maximum_sentence_words"] == "warn", "Long sentence check did not warn.");
        AssertNoLeaks(failingAudit.RootElement.GetRawText(), workspaceRoot, cleanContent, failingContent);
    }

    await SendGateAsync(quality, 9, failingArtifact, minimumWords: 10, maximumWarnings: 0, blockOnPlaceholders: true);
    using (var failingGate = await quality.ReadJsonAsync())
    {
        var data = failingGate.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data");
        Require(data.GetProperty("decision").GetString() == "BLOCKED", "Failing draft gate did not block.");
        var reasons = data.GetProperty("blockingReasons").EnumerateArray().Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
        Require(reasons.Contains("content.no_placeholders"), "Placeholder blocking reason missing.");
        Require(reasons.Contains("quality.maximum_warnings"), "Maximum warnings blocking reason missing.");
    }

    await quality.SendRequestAsync(
        10,
        "tools/call",
        new
        {
            name = "book.audit.run",
            arguments = new
            {
                projectId = "other",
                payload = new { artifactId = cleanArtifact, version = 1 },
            },
        });
    using (var scope = await quality.ReadJsonAsync())
    {
        AssertToolError(scope.RootElement, "quality_scope_violation");
    }

    await quality.SendRequestAsync(
        11,
        "tools/call",
        new
        {
            name = "book.repair.propose",
            arguments = new { projectId = "demo", payload = new { artifactId = cleanArtifact, version = 1 } },
        });
    using (var reserved = await quality.ReadJsonAsync())
    {
        Require(reserved.RootElement.GetProperty("error").GetProperty("code").GetInt32() == -32602, "Reserved quality tool was not rejected.");
    }

    var completion = await quality.CloseInputAndWaitAsync();
    Require(completion.ExitCode == 0, "Quality MCP process did not exit cleanly.");
    Require(string.IsNullOrWhiteSpace(completion.RemainingStdout), "Quality MCP stdout contained unexpected content.");
    ValidateSafeStderr(completion.Stderr, workspaceRoot, cleanContent, failingContent);
}

static Task SendRegisterAsync(McpChildProcess process, int id, string artifactId, string content) =>
    process.SendRequestAsync(
        id,
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
                    content,
                },
            },
        });

static Task SendAuditAsync(
    McpChildProcess process,
    int id,
    string artifactId,
    int minimumWords,
    int maximumSentenceWords) =>
    process.SendRequestAsync(
        id,
        "tools/call",
        new
        {
            name = "book.audit.run",
            arguments = new
            {
                projectId = "demo",
                payload = new { artifactId, version = 1, minimumWords, maximumSentenceWords },
            },
        });

static Task SendGateAsync(
    McpChildProcess process,
    int id,
    string artifactId,
    int minimumWords,
    int maximumWarnings,
    bool blockOnPlaceholders) =>
    process.SendRequestAsync(
        id,
        "tools/call",
        new
        {
            name = "book.gate.evaluate",
            arguments = new
            {
                projectId = "demo",
                payload = new
                {
                    artifactId,
                    version = 1,
                    profile = "draft-basic",
                    minimumWords,
                    maximumWarnings,
                    blockOnPlaceholders,
                },
            },
        });

static async Task InitializeAndReadyAsync(McpChildProcess process, int id, string clientName)
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

static string[] Inventory(string workspaceRoot) =>
    Directory.Exists(workspaceRoot)
        ? Directory.EnumerateFiles(workspaceRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(workspaceRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray()
        : [];

static void AssertToolError(JsonElement response, string code)
{
    var result = response.GetProperty("result");
    Require(result.GetProperty("isError").GetBoolean(), "Expected a quality tool error.");
    Require(result.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString() == code, $"Expected quality error {code}.");
}

static void AssertNoLeaks(string json, string workspaceRoot, params string[] draftContents)
{
    Require(!json.Contains(workspaceRoot, StringComparison.OrdinalIgnoreCase), "Quality result leaked the workspace path.");
    Require(!json.Contains(".bookstudio", StringComparison.OrdinalIgnoreCase), "Quality result leaked an Artifact Store path.");
    foreach (var content in draftContents)
    {
        Require(!json.Contains(content, StringComparison.Ordinal), "Quality result leaked full draft content.");
    }
}

static void ValidateSafeStderr(string stderr, params string[] forbidden)
{
    foreach (var value in forbidden)
    {
        Require(!stderr.Contains(value, StringComparison.OrdinalIgnoreCase), "stderr leaked a forbidden value.");
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

internal sealed class McpChildProcess : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly Process _process;
    private bool _inputClosed;

    private McpChildProcess(Process process) => _process = process;

    public static McpChildProcess Start(string assemblyName, string workspaceRoot)
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, assemblyName);
        if (!File.Exists(assembly))
        {
            throw new FileNotFoundException("MCP child assembly is missing from integration output.", assembly);
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
        return new McpChildProcess(
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("The MCP child process could not be started."));
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
