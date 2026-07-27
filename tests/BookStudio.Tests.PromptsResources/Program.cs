using System.Diagnostics;
using System.Text;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "bookstudio-prompts-" + Guid.NewGuid().ToString("N"));
var servers = new[]
{
    new ServerCase(
        "BookStudio.Mcp.dll",
        "bookstudio",
        "book.core.inspect-artifact.v1",
        "book://prompts/book-core/inspect-artifact/v1",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectId"] = "demo",
            ["artifactId"] = "demo.chapter-01",
            ["version"] = "1",
        },
        MissingArgument: "version"),
    new ServerCase(
        "BookStudio.Mcp.Authoring.dll",
        "bookstudio-authoring",
        "book.authoring.validate-draft.v1",
        "book://prompts/book-authoring/validate-draft/v1",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectId"] = "demo",
            ["artifactId"] = "demo.draft.chapter-01",
            ["version"] = "1",
        },
        MissingArgument: "version"),
    new ServerCase(
        "BookStudio.Mcp.Quality.dll",
        "bookstudio-quality",
        "book.quality.assess-draft.v1",
        "book://prompts/book-quality/assess-draft/v1",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectId"] = "demo",
            ["artifactId"] = "demo.draft.chapter-01",
            ["version"] = "1",
        },
        MissingArgument: "version"),
    new ServerCase(
        "BookStudio.Mcp.Production.dll",
        "bookstudio-production",
        "book.production.preflight-release.v1",
        "book://prompts/book-production/preflight-release/v1",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectId"] = "demo",
            ["releaseArtifactId"] = "demo.release.proof-01",
            ["version"] = "1",
        },
        MissingArgument: "version"),
    new ServerCase(
        "BookStudio.Mcp.Ops.dll",
        "bookstudio-ops",
        "book.ops.inspect-readiness.v1",
        "book://prompts/book-ops/inspect-readiness/v1",
        new Dictionary<string, string>(StringComparer.Ordinal),
        MissingArgument: null),
};

try
{
    foreach (var serverCase in servers)
    {
        await VerifyServerAsync(serverCase, Path.Combine(root, serverCase.ExpectedServerName));
    }

    Console.WriteLine("PROMPTS_RESOURCES_INTEGRATION_PASS");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("PROMPTS_RESOURCES_INTEGRATION_FAIL: " + exception);
    return 1;
}
finally
{
    TryDelete(root);
}

static async Task VerifyServerAsync(ServerCase serverCase, string workspaceRoot)
{
    Require(!Directory.Exists(workspaceRoot), $"{serverCase.ExpectedServerName}: workspace exists before launch.");
    await using var server = ChildProcess.Start(serverCase.AssemblyName, workspaceRoot);

    await server.SendRequestAsync(
        1,
        "initialize",
        new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "prompts-resources-integration", version = "1.0.0" },
        });
    using (var initialized = await server.ReadJsonAsync())
    {
        var result = initialized.RootElement.GetProperty("result");
        Require(
            result.GetProperty("serverInfo").GetProperty("name").GetString() == serverCase.ExpectedServerName,
            $"{serverCase.ExpectedServerName}: server identity mismatch.");
        var capabilityNames = result.GetProperty("capabilities")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(
            capabilityNames.SequenceEqual(new[] { "prompts", "resources", "tools" }),
            $"{serverCase.ExpectedServerName}: capability surface mismatch.");
        Require(
            !result.GetProperty("capabilities").GetProperty("prompts").GetProperty("listChanged").GetBoolean(),
            $"{serverCase.ExpectedServerName}: prompts.listChanged mismatch.");
        Require(
            result.GetProperty("instructions").GetString()?.Contains("user-controlled", StringComparison.OrdinalIgnoreCase) == true,
            $"{serverCase.ExpectedServerName}: prompt safety instructions missing.");
    }
    await server.SendNotificationAsync("notifications/initialized", new { });

    await server.SendRequestAsync(2, "prompts/list", new { });
    string[] argumentNames;
    using (var listed = await server.ReadJsonAsync())
    {
        var result = listed.RootElement.GetProperty("result");
        var prompts = result.GetProperty("prompts");
        Require(prompts.GetArrayLength() == 1, $"{serverCase.ExpectedServerName}: expected one active prompt.");
        var prompt = prompts[0];
        Require(prompt.GetProperty("name").GetString() == serverCase.PromptName, $"{serverCase.ExpectedServerName}: prompt name mismatch.");
        argumentNames = prompt.TryGetProperty("arguments", out var definitions)
            ? definitions.EnumerateArray().Select(item => item.GetProperty("name").GetString() ?? string.Empty).ToArray()
            : [];
        Require(
            argumentNames.SequenceEqual(serverCase.Arguments.Keys.Order(StringComparer.Ordinal)),
            $"{serverCase.ExpectedServerName}: prompt argument definitions mismatch.");
        Require(!result.TryGetProperty("nextCursor", out _), $"{serverCase.ExpectedServerName}: unexpected prompt cursor.");
    }

    await server.SendRequestAsync(
        3,
        "prompts/get",
        new { name = serverCase.PromptName, arguments = serverCase.Arguments });
    string renderedText;
    using (var promptResult = await server.ReadJsonAsync())
    {
        var result = promptResult.RootElement.GetProperty("result");
        Require(!string.IsNullOrWhiteSpace(result.GetProperty("description").GetString()), $"{serverCase.ExpectedServerName}: prompt description missing.");
        var messages = result.GetProperty("messages");
        Require(messages.GetArrayLength() >= 1, $"{serverCase.ExpectedServerName}: prompt messages missing.");
        Require(messages[0].GetProperty("role").GetString() == "user", $"{serverCase.ExpectedServerName}: prompt role mismatch.");
        var content = messages[0].GetProperty("content");
        Require(content.GetProperty("type").GetString() == "text", $"{serverCase.ExpectedServerName}: prompt content type mismatch.");
        renderedText = content.GetProperty("text").GetString() ?? string.Empty;
        Require(renderedText.Length is > 0 and <= 4096, $"{serverCase.ExpectedServerName}: rendered prompt bounds mismatch.");
        foreach (var value in serverCase.Arguments.Values)
        {
            Require(renderedText.Contains(value, StringComparison.Ordinal), $"{serverCase.ExpectedServerName}: rendered argument missing.");
        }
        Require(!renderedText.Contains("{{", StringComparison.Ordinal), $"{serverCase.ExpectedServerName}: unresolved template token.");
    }

    var resourceUris = new List<string>();
    string? cursor = null;
    var requestId = 4;
    do
    {
        await server.SendRequestAsync(
            requestId++,
            "resources/list",
            cursor is null ? new { } : new { cursor });
        using var resources = await server.ReadJsonAsync();
        var result = resources.RootElement.GetProperty("result");
        resourceUris.AddRange(result.GetProperty("resources").EnumerateArray()
            .Select(resource => resource.GetProperty("uri").GetString() ?? string.Empty));
        cursor = result.TryGetProperty("nextCursor", out var next)
            ? next.GetString()
            : null;
    }
    while (cursor is not null);

    Require(resourceUris.Contains(serverCase.ResourceUri, StringComparer.Ordinal), $"{serverCase.ExpectedServerName}: prompt resource not listed.");
    Require(resourceUris.Count == resourceUris.Distinct(StringComparer.Ordinal).Count(), $"{serverCase.ExpectedServerName}: duplicate resources listed.");
    Require(resourceUris.SequenceEqual(resourceUris.Order(StringComparer.Ordinal)), $"{serverCase.ExpectedServerName}: resources not sorted.");

    await server.SendRequestAsync(requestId++, "resources/read", new { uri = serverCase.ResourceUri });
    using (var resource = await server.ReadJsonAsync())
    {
        var content = resource.RootElement.GetProperty("result").GetProperty("contents")[0];
        Require(
            content.GetProperty("mimeType").GetString() == "application/vnd.bookstudio.prompt-template+json",
            $"{serverCase.ExpectedServerName}: prompt resource media type mismatch.");
        using var definition = JsonDocument.Parse(content.GetProperty("text").GetString() ?? string.Empty);
        var root = definition.RootElement;
        Require(root.GetProperty("name").GetString() == serverCase.PromptName, $"{serverCase.ExpectedServerName}: resource name parity mismatch.");
        Require(root.GetProperty("promptVersion").GetString() == "1", $"{serverCase.ExpectedServerName}: prompt resource version mismatch.");
        var resourceArguments = root.GetProperty("arguments").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString() ?? string.Empty)
            .ToArray();
        Require(resourceArguments.SequenceEqual(argumentNames), $"{serverCase.ExpectedServerName}: resource argument parity mismatch.");
        var template = root.GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString() ?? string.Empty;
        Require(!string.IsNullOrWhiteSpace(template), $"{serverCase.ExpectedServerName}: resource template missing.");
    }

    if (serverCase.MissingArgument is not null)
    {
        var incomplete = serverCase.Arguments
            .Where(pair => !string.Equals(pair.Key, serverCase.MissingArgument, StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        await server.SendRequestAsync(
            requestId++,
            "prompts/get",
            new { name = serverCase.PromptName, arguments = incomplete });
        using var missing = await server.ReadJsonAsync();
        AssertInvalidParams(missing.RootElement, $"{serverCase.ExpectedServerName}: missing argument accepted.");
    }
    else
    {
        await server.SendRequestAsync(
            requestId++,
            "prompts/get",
            new
            {
                name = serverCase.PromptName,
                arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["extra"] = "forbidden" },
            });
        using var extraForNoArg = await server.ReadJsonAsync();
        AssertInvalidParams(extraForNoArg.RootElement, $"{serverCase.ExpectedServerName}: extra no-arg value accepted.");
    }

    var extraArguments = serverCase.Arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    extraArguments["unexpected"] = "value";
    await server.SendRequestAsync(
        requestId++,
        "prompts/get",
        new { name = serverCase.PromptName, arguments = extraArguments });
    using (var extra = await server.ReadJsonAsync())
    {
        AssertInvalidParams(extra.RootElement, $"{serverCase.ExpectedServerName}: extra argument accepted.");
    }

    await server.SendRequestAsync(
        requestId++,
        "prompts/get",
        new { name = "book.unknown.prompt.v1", arguments = new Dictionary<string, string>() });
    using (var unknown = await server.ReadJsonAsync())
    {
        AssertInvalidParams(unknown.RootElement, $"{serverCase.ExpectedServerName}: unknown prompt accepted.");
    }

    Require(!Directory.Exists(workspaceRoot), $"{serverCase.ExpectedServerName}: prompt discovery created workspace.");
    var completion = await server.CloseInputAndWaitAsync();
    Require(completion.ExitCode == 0, $"{serverCase.ExpectedServerName}: process exit mismatch.");
    Require(string.IsNullOrWhiteSpace(completion.RemainingStdout), $"{serverCase.ExpectedServerName}: unexpected stdout.");
    ValidateSafeStderr(completion.Stderr, workspaceRoot, renderedText);
}

static void AssertInvalidParams(JsonElement response, string message)
{
    Require(response.GetProperty("error").GetProperty("code").GetInt32() == -32602, message);
}

static void ValidateSafeStderr(string stderr, params string[] forbiddenValues)
{
    foreach (var forbidden in forbiddenValues)
    {
        Require(!stderr.Contains(forbidden, StringComparison.OrdinalIgnoreCase), "stderr leaked prompt data or workspace path.");
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

internal sealed record ServerCase(
    string AssemblyName,
    string ExpectedServerName,
    string PromptName,
    string ResourceUri,
    IReadOnlyDictionary<string, string> Arguments,
    string? MissingArgument);

internal sealed class ChildProcess : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly Process _process;
    private bool _inputClosed;

    private ChildProcess(Process process)
    {
        _process = process;
    }

    public static ChildProcess Start(string assemblyName, string workspaceRoot)
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, assemblyName);
        if (!File.Exists(assembly))
        {
            throw new FileNotFoundException("Bounded MCP assembly is missing from integration output.", assembly);
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
        return new ChildProcess(Process.Start(startInfo)
            ?? throw new InvalidOperationException("Bounded MCP child process could not be started."));
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
            throw new InvalidOperationException("MCP child ended before response. stderr=" + stderr);
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

    public async Task<Completion> CloseInputAndWaitAsync()
    {
        if (!_inputClosed)
        {
            _inputClosed = true;
            _process.StandardInput.Close();
        }
        using var timeout = new CancellationTokenSource(Timeout);
        await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        return new Completion(
            _process.ExitCode,
            await _process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false),
            await _process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false));
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

internal sealed record Completion(int ExitCode, string RemainingStdout, string Stderr);
