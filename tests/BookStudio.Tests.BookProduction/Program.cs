using System.Diagnostics;
using System.Text;
using System.Text.Json;

var workspace = Path.Combine(Path.GetTempPath(), "bookstudio-production-" + Guid.NewGuid().ToString("N"));
var lazy = workspace + "-lazy";
const string manuscript = "demo.draft.manuscript";
const string coverText = "demo.draft.cover-text";
const string manuscriptContent = "# Production Manuscript\n\nA verified manuscript source for deterministic release preparation.\n";
const string coverFixture = "This text intentionally has the wrong media type for a cover role.";
try
{
    await VerifyLazyAsync(lazy);
    await RegisterSourcesAsync(workspace);
    await VerifyProductionAsync(workspace);
    Console.WriteLine("BOOK_PRODUCTION_INTEGRATION_PASS");
    return 0;
}
finally
{
    TryDelete(workspace);
    TryDelete(lazy);
}

async Task RegisterSourcesAsync(string root)
{
    await using var authoring = Child.Start("BookStudio.Mcp.Authoring.dll", root);
    await ReadyAsync(authoring, 1, "production-authoring-client");
    await RegisterAsync(authoring, 2, manuscript, manuscriptContent);
    using (var response = await authoring.ReadAsync())
    {
        Require(!response.RootElement.GetProperty("result").GetProperty("isError").GetBoolean(), "Manuscript registration failed.");
    }
    await RegisterAsync(authoring, 3, coverText, coverFixture);
    using (var response = await authoring.ReadAsync())
    {
        Require(!response.RootElement.GetProperty("result").GetProperty("isError").GetBoolean(), "Cover fixture registration failed.");
    }
    var completion = await authoring.CloseAsync();
    Require(completion.ExitCode == 0, "Authoring fixture process failed.");
    SafeStderr(completion.Stderr, root, manuscriptContent, coverFixture);
}

async Task VerifyLazyAsync(string root)
{
    Require(!Directory.Exists(root), "Lazy production workspace already exists.");
    await using var production = Child.Start("BookStudio.Mcp.Production.dll", root);
    await ReadyAsync(production, 1, "lazy-production-client");
    await production.SendRequestAsync(2, "tools/list", new { });
    using (var response = await production.ReadAsync())
    {
        Require(response.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() == 2, "Production tool count mismatch.");
    }
    Require(!Directory.Exists(root), "Production initialize/list created workspace.");
    var completion = await production.CloseAsync();
    Require(completion.ExitCode == 0, "Lazy production process failed.");
    Require(string.IsNullOrWhiteSpace(completion.Stdout), "Lazy production stdout contained extra output.");
    SafeStderr(completion.Stderr, root);
}

async Task VerifyProductionAsync(string root)
{
    await using var production = Child.Start("BookStudio.Mcp.Production.dll", root);
    await production.SendRequestAsync(1, "initialize", new
    {
        protocolVersion = "2025-11-25",
        capabilities = new { },
        clientInfo = new { name = "production-integration", version = "1.0.0" },
    });
    using (var response = await production.ReadAsync())
    {
        var result = response.RootElement.GetProperty("result");
        Require(result.GetProperty("serverInfo").GetProperty("name").GetString() == "bookstudio-production", "Production identity mismatch.");
        Require(result.GetProperty("serverInfo").GetProperty("title").GetString() == "BookStudio Production MCP", "Production title mismatch.");
        var capabilities = result.GetProperty("capabilities");
        Require(
            capabilities.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[] { "prompts", "resources", "tools" }),
            "Production capabilities are not exact.");
        Require(!capabilities.GetProperty("prompts").GetProperty("listChanged").GetBoolean(), "Production prompts.listChanged must be false.");
    }
    await production.SendNotificationAsync("notifications/initialized", new { });

    await production.SendRequestAsync(2, "tools/list", new { });
    using (var response = await production.ReadAsync())
    {
        var tools = response.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Require(tools.Select(tool => tool.GetProperty("name").GetString()).SequenceEqual(new[] { "book.preflight.run", "book.release.prepare" }), "Production tool surface mismatch.");
        Require(!response.RootElement.GetRawText().Contains("book.render.preview", StringComparison.Ordinal), "Reserved render tool was advertised.");
    }

    var resourceUris = new List<string>();
    string? cursor = null;
    var resourceRequestId = 1000;
    do
    {
        if (cursor is null)
        {
            await production.SendRequestAsync(resourceRequestId++, "resources/list", new { });
        }
        else
        {
            await production.SendRequestAsync(resourceRequestId++, "resources/list", new { cursor });
        }
        using var resourcePage = await production.ReadAsync();
        var result = resourcePage.RootElement.GetProperty("result");
        var page = result.GetProperty("resources").EnumerateArray().ToArray();
        Require(page.Length > 0, "Production resource pagination returned an empty page.");
        resourceUris.AddRange(page.Select(item => item.GetProperty("uri").GetString()!));
        cursor = result.TryGetProperty("nextCursor", out var nextCursor)
            ? nextCursor.GetString()
            : null;
        Require(resourceRequestId <= 1020, "Production resource pagination did not terminate.");
    }
    while (cursor is not null);
    Require(resourceUris.Count == 9, "Production merged resource count mismatch.");
    Require(resourceUris.SequenceEqual(resourceUris.OrderBy(uri => uri, StringComparer.Ordinal)), "Production resources are not ordinally sorted.");
    Require(resourceUris.Contains("book://production/profiles/release-basic"), "release-basic profile missing.");
    Require(resourceUris.Contains("book://prompts/book-production/preflight-release/v1"), "Production prompt resource missing.");
    Require(resourceUris.Contains("book://security/sandbox-policy"), "Production sandbox policy resource is missing.");

    await production.SendRequestAsync(5, "resources/read", new { uri = "book://production/profiles/release-basic" });
    using (var profile = await production.ReadAsync())
    {
        Require(profile.RootElement.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!.Contains("release.role_media_compatibility", StringComparison.Ordinal), "Production profile mismatch.");
    }

    await PrepareAsync(production, 6, "proof-good", "Demo Proof", new[]
    {
        new { role = "manuscript", artifactId = manuscript, version = 1 },
    });
    using (var prepared = await production.ReadAsync())
    {
        var result = prepared.RootElement.GetProperty("result");
        Require(!result.GetProperty("isError").GetBoolean(), "Good release preparation failed.");
        NoLeaks(prepared.RootElement.GetRawText(), root, manuscriptContent, coverFixture);
    }
    var inventoryAfterPrepare = Inventory(root);

    await PreflightAsync(production, 7, "demo.release.proof-good");
    using (var preflight = await production.ReadAsync())
    {
        var data = preflight.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data");
        Require(data.GetProperty("decision").GetString() == "PASS", "Good release preflight did not pass.");
        Require(data.GetProperty("checks").EnumerateArray().All(check => check.GetProperty("status").GetString() == "pass"), "Good release contains failing checks.");
        NoLeaks(preflight.RootElement.GetRawText(), root, manuscriptContent, coverFixture);
    }
    Require(inventoryAfterPrepare.SequenceEqual(Inventory(root)), "Preflight mutated the workspace.");

    await PrepareAsync(production, 8, "proof-good", "Demo Proof", new[]
    {
        new { role = "manuscript", artifactId = manuscript, version = 1 },
    });
    using (var conflict = await production.ReadAsync())
    {
        ToolError(conflict.RootElement, "release_version_conflict");
    }

    await PrepareAsync(production, 9, "proof-bad", "Bad Proof", new[]
    {
        new { role = "manuscript", artifactId = manuscript, version = 1 },
        new { role = "cover", artifactId = coverText, version = 1 },
    });
    using (var preparedBad = await production.ReadAsync())
    {
        Require(!preparedBad.RootElement.GetProperty("result").GetProperty("isError").GetBoolean(), "Incompatible fixture release preparation failed unexpectedly.");
    }
    var inventoryBeforeBadPreflight = Inventory(root);
    await PreflightAsync(production, 10, "demo.release.proof-bad");
    using (var preflightBad = await production.ReadAsync())
    {
        var data = preflightBad.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data");
        Require(data.GetProperty("decision").GetString() == "BLOCKED", "Incompatible release preflight did not block.");
        var reasons = data.GetProperty("blockingReasons").EnumerateArray().Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
        Require(reasons.Contains("release.role_media_compatibility"), "Role/media blocking reason missing.");
    }
    Require(inventoryBeforeBadPreflight.SequenceEqual(Inventory(root)), "Blocked preflight mutated the workspace.");

    await production.SendRequestAsync(11, "tools/call", new
    {
        name = "book.preflight.run",
        arguments = new { projectId = "other", payload = new { releaseArtifactId = "demo.release.proof-good", version = 1, profile = "release-basic" } },
    });
    using (var scope = await production.ReadAsync())
    {
        ToolError(scope.RootElement, "release_scope_violation");
    }

    await production.SendRequestAsync(12, "tools/call", new
    {
        name = "book.render.preview",
        arguments = new { projectId = "demo", payload = new { releaseArtifactId = "demo.release.proof-good", version = 1 } },
    });
    using (var reserved = await production.ReadAsync())
    {
        Require(reserved.RootElement.GetProperty("error").GetProperty("code").GetInt32() == -32602, "Reserved render tool was not rejected.");
    }

    var completion = await production.CloseAsync();
    Require(completion.ExitCode == 0, "Production process failed.");
    Require(string.IsNullOrWhiteSpace(completion.Stdout), "Production stdout contained extra output.");
    SafeStderr(completion.Stderr, root, manuscriptContent, coverFixture);
}

Task RegisterAsync(Child child, int id, string artifactId, string content) =>
    child.SendRequestAsync(id, "tools/call", new
    {
        name = "book.draft.register",
        arguments = new { projectId = "demo", payload = new { artifactId, expectedVersion = 1, mediaType = "text/markdown", content } },
    });

Task PrepareAsync(Child child, int id, string releaseId, string title, object[] sources) =>
    child.SendRequestAsync(id, "tools/call", new
    {
        name = "book.release.prepare",
        arguments = new { projectId = "demo", payload = new { releaseId, expectedVersion = 1, title, language = "es-ES", sources } },
    });

Task PreflightAsync(Child child, int id, string releaseArtifactId) =>
    child.SendRequestAsync(id, "tools/call", new
    {
        name = "book.preflight.run",
        arguments = new { projectId = "demo", payload = new { releaseArtifactId, version = 1, profile = "release-basic" } },
    });

static async Task ReadyAsync(Child child, int id, string client)
{
    await child.SendRequestAsync(id, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = client, version = "1.0.0" } });
    using var response = await child.ReadAsync();
    Require(response.RootElement.GetProperty("id").GetInt32() == id, "Initialize ID mismatch.");
    await child.SendNotificationAsync("notifications/initialized", new { });
}

static string[] Inventory(string root) => Directory.Exists(root)
    ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray()
    : [];

static void ToolError(JsonElement response, string code)
{
    var result = response.GetProperty("result");
    Require(result.GetProperty("isError").GetBoolean(), "Expected tool error.");
    Require(result.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString() == code, $"Expected {code}.");
}

static void NoLeaks(string json, string root, params string[] sourceContent)
{
    Require(!json.Contains(root, StringComparison.OrdinalIgnoreCase), "Response leaked workspace path.");
    Require(!json.Contains("/.bookstudio/", StringComparison.OrdinalIgnoreCase), "Response leaked Linux store path.");
    Require(!json.Contains("\\.bookstudio\\", StringComparison.OrdinalIgnoreCase), "Response leaked JSON-escaped Windows store path.");
    foreach (var content in sourceContent) Require(!json.Contains(content, StringComparison.Ordinal), "Response leaked source content.");
}

static void SafeStderr(string stderr, params string[] forbidden)
{
    foreach (var value in forbidden) Require(!stderr.Contains(value, StringComparison.OrdinalIgnoreCase), "stderr leaked forbidden content.");
    foreach (var line in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
    {
        Require(line.Length <= 96, "stderr line too long.");
        Require(line.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'), "stderr contained non-diagnostic content.");
    }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void TryDelete(string path)
{
    try { if (Directory.Exists(path)) Directory.Delete(path, true); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

internal sealed class Child : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly Process _process;
    private bool _closed;
    private Child(Process process) => _process = process;

    public static Child Start(string assemblyName, string root)
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, assemblyName);
        if (!File.Exists(assembly)) throw new FileNotFoundException("MCP child assembly missing.", assembly);
        var info = new ProcessStartInfo
        {
            FileName = "dotnet", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
        };
        info.ArgumentList.Add(assembly); info.ArgumentList.Add("--workspace-root"); info.ArgumentList.Add(root);
        return new Child(Process.Start(info) ?? throw new InvalidOperationException("Could not start MCP child."));
    }

    public Task SendRequestAsync(int id, string method, object parameters) => SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters });
    public Task SendNotificationAsync(string method, object parameters) => SendAsync(new { jsonrpc = "2.0", method, @params = parameters });
    private async Task SendAsync(object message)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await _process.StandardInput.FlushAsync();
    }
    public async Task<JsonDocument> ReadAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var line = await _process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("MCP ended before response: " + await _process.StandardError.ReadToEndAsync(timeout.Token));
        return JsonDocument.Parse(line);
    }
    public async Task<Completion> CloseAsync()
    {
        if (!_closed) { _closed = true; _process.StandardInput.Close(); }
        using var timeout = new CancellationTokenSource(Timeout);
        await _process.WaitForExitAsync(timeout.Token);
        return new Completion(_process.ExitCode, await _process.StandardOutput.ReadToEndAsync(timeout.Token), await _process.StandardError.ReadToEndAsync(timeout.Token));
    }
    public async ValueTask DisposeAsync()
    {
        if (!_closed) { _closed = true; _process.StandardInput.Close(); }
        if (!_process.HasExited)
        {
            try { using var timeout = new CancellationTokenSource(Timeout); await _process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { _process.Kill(true); await _process.WaitForExitAsync(); }
        }
        _process.Dispose();
    }
}

internal sealed record Completion(int ExitCode, string Stdout, string Stderr);
