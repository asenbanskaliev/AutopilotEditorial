using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Mcp.Transport;

namespace BookStudio.Tests.McpConformance;

internal sealed class McpConformanceRunner
{
    public const int FuzzSeed = 27027;
    public const int FuzzCasesPerServer = 128;

    private const string Canary = "mcp-conformance-secret-canary-27027";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyList<McpServerDescriptor> Servers =
    [
        new("BookStudio.Mcp.dll", "bookstudio", "BookStudio MCP"),
        new("BookStudio.Mcp.Authoring.dll", "bookstudio-authoring", "BookStudio Authoring MCP"),
        new("BookStudio.Mcp.Quality.dll", "bookstudio-quality", "BookStudio Quality MCP"),
        new("BookStudio.Mcp.Production.dll", "bookstudio-production", "BookStudio Production MCP"),
        new("BookStudio.Mcp.Ops.dll", "bookstudio-ops", "BookStudio Operations MCP"),
    ];

    public async Task<McpConformanceReport> RunAsync()
    {
        var corpus = LoadCorpus();
        ValidateCorpus(corpus);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var root = Path.Combine(
            Path.GetTempPath(),
            "bookstudio-mcp-conformance-" + Guid.NewGuid().ToString("N"));
        try
        {
            for (var index = 0; index < Servers.Count; index++)
            {
                await RunServerAsync(
                        Servers[index],
                        index,
                        corpus,
                        hash,
                        Path.Combine(root, index.ToString(), "workspace"))
                    .ConfigureAwait(false);
            }

            var digest = Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant();
            return new McpConformanceReport(
                Servers.Count,
                corpus.Cases.Count,
                Servers.Count * FuzzCasesPerServer,
                FuzzSeed,
                digest);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task RunServerAsync(
        McpServerDescriptor server,
        int serverIndex,
        McpConformanceCorpus corpus,
        IncrementalHash hash,
        string workspaceRoot)
    {
        Require(!Directory.Exists(workspaceRoot), $"{server.Name}: workspace exists before launch.");
        await using var process = McpProcessDriver.Start(server.AssemblyName, workspaceRoot);

        await process.SendRequestAsync("pre-ping", "ping").ConfigureAwait(false);
        using (var ping = await process.ReadJsonAsync().ConfigureAwait(false))
        {
            ExpectResult(ping.RootElement, "pre-ping", server.Name + ": pre-initialize ping");
        }

        await RunCorpusPhaseAsync(process, corpus, "created", server.Name)
            .ConfigureAwait(false);

        var deepPayload = new string('[', 65) + "0" + new string(']', 65);
        await process.SendRawAsync(deepPayload).ConfigureAwait(false);
        using (var deep = await process.ReadJsonAsync().ConfigureAwait(false))
        {
            ExpectError(deep.RootElement, -32700, null, server.Name + ": maximum JSON depth");
        }

        await process.SendNotificationAsync(
                "initialize",
                InitializeParameters(corpus.ProtocolVersion, "notification-client"))
            .ConfigureAwait(false);
        await process.SendRequestAsync("after-init-notification", "ping").ConfigureAwait(false);
        using (var afterNotification = await process.ReadJsonAsync().ConfigureAwait(false))
        {
            ExpectResult(
                afterNotification.RootElement,
                "after-init-notification",
                server.Name + ": initialize notification recovery");
        }

        await process.SendRequestAsync(
                "initialize",
                "initialize",
                InitializeParameters(corpus.ProtocolVersion, "conformance-client"))
            .ConfigureAwait(false);
        using (var initialize = await process.ReadJsonAsync().ConfigureAwait(false))
        {
            ValidateInitialize(initialize.RootElement, server, corpus.ProtocolVersion);
        }

        await process.SendRequestAsync(
                "duplicate-initialize",
                "initialize",
                InitializeParameters(corpus.ProtocolVersion, "duplicate-client"))
            .ConfigureAwait(false);
        using (var duplicateInitialize = await process.ReadJsonAsync().ConfigureAwait(false))
        {
            ExpectError(
                duplicateInitialize.RootElement,
                -32600,
                "duplicate-initialize",
                server.Name + ": duplicate initialize");
        }

        await process.SendNotificationAsync("notifications/initialized")
            .ConfigureAwait(false);
        await process.SendNotificationAsync("notifications/initialized")
            .ConfigureAwait(false);
        await process.SendRequestAsync("after-duplicate-initialized", "ping")
            .ConfigureAwait(false);
        using (var readyPing = await process.ReadJsonAsync().ConfigureAwait(false))
        {
            ExpectResult(
                readyPing.RootElement,
                "after-duplicate-initialized",
                server.Name + ": duplicate initialized notification recovery");
        }

        await ValidateFeatureListAsync(process, "valid-tools", "tools/list", "tools", server.Name)
            .ConfigureAwait(false);
        await ValidateFeatureListAsync(process, "valid-resources", "resources/list", "resources", server.Name)
            .ConfigureAwait(false);
        await ValidateFeatureListAsync(process, "valid-prompts", "prompts/list", "prompts", server.Name)
            .ConfigureAwait(false);

        if (server.Name == "bookstudio-ops")
        {
            await ValidateTolerantToolCallAsync(process, server.Name)
                .ConfigureAwait(false);
        }

        await RunCorpusPhaseAsync(process, corpus, "ready", server.Name)
            .ConfigureAwait(false);

        await process.SendRequestAsync("duplicate-request", "ping").ConfigureAwait(false);
        using (var firstDuplicate = await process.ReadJsonAsync().ConfigureAwait(false))
        {
            ExpectResult(firstDuplicate.RootElement, "duplicate-request", server.Name + ": first duplicate-id request");
        }
        await process.SendRequestAsync("duplicate-request", "ping").ConfigureAwait(false);
        using (var duplicate = await process.ReadJsonAsync().ConfigureAwait(false))
        {
            ExpectError(duplicate.RootElement, -32600, "duplicate-request", server.Name + ": reused request id");
        }

        await process.SendNotificationAsync(
                "notifications/conformance",
                new { token = Canary })
            .ConfigureAwait(false);
        await process.SendRequestAsync("after-unknown-notification", "ping")
            .ConfigureAwait(false);
        using (var notificationPing = await process.ReadJsonAsync().ConfigureAwait(false))
        {
            ExpectResult(
                notificationPing.RootElement,
                "after-unknown-notification",
                server.Name + ": unknown notification recovery");
        }

        await RunDeterministicFuzzAsync(process, server, serverIndex, hash)
            .ConfigureAwait(false);

        await process.SendRawAsync(
                new string('x', StdioJsonRpcServer.MaximumMessageBytes + 1))
            .ConfigureAwait(false);
        using (var oversize = await process.ReadJsonAsync().ConfigureAwait(false))
        {
            ExpectError(oversize.RootElement, -32600, null, server.Name + ": oversize message");
        }

        await process.SendRequestAsync("final-ping", "ping").ConfigureAwait(false);
        using (var finalPing = await process.ReadJsonAsync().ConfigureAwait(false))
        {
            ExpectResult(finalPing.RootElement, "final-ping", server.Name + ": final survival ping");
        }

        var completion = await process.CloseAsync().ConfigureAwait(false);
        Require(completion.ExitCode == 0, $"{server.Name}: exit code was {completion.ExitCode}.");
        Require(
            string.IsNullOrWhiteSpace(completion.RemainingStdout),
            $"{server.Name}: stdout contained an extra response.");
        ValidateSafeStderr(server.Name, completion.Stderr, workspaceRoot);
        Require(
            !Directory.Exists(workspaceRoot),
            $"{server.Name}: conformance-only operations created the workspace.");
    }

    private static async Task RunCorpusPhaseAsync(
        McpProcessDriver process,
        McpConformanceCorpus corpus,
        string phase,
        string serverName)
    {
        foreach (var testCase in corpus.Cases.Where(item => item.Phase == phase))
        {
            await process.SendRawAsync(testCase.Payload).ConfigureAwait(false);
            using var response = await process.ReadJsonAsync().ConfigureAwait(false);
            ExpectError(
                response.RootElement,
                testCase.ExpectedCode,
                testCase.ExpectedId,
                serverName + ": corpus " + testCase.Id);
        }
    }

    private static async Task ValidateTolerantToolCallAsync(
        McpProcessDriver process,
        string serverName)
    {
        // Regression: tools/call must accept optional "arguments" (omitted for no-arg tools)
        // and tolerate extra top-level params keys such as "_meta" (MCP spec allows both).
        // A strict "exactly name and object arguments" gate broke real clients with -32602.
        await process.SendRawAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":\"tolerant-call\",\"method\":\"tools/call\",\"params\":{\"name\":\"book.ops.diagnostics\",\"_meta\":{\"progressToken\":\"tolerant\"}}}")
            .ConfigureAwait(false);
        using var response = await process.ReadJsonAsync().ConfigureAwait(false);
        ExpectResult(
            response.RootElement,
            "tolerant-call",
            serverName + ": tools/call with optional arguments and extra _meta");
    }

    private static async Task ValidateFeatureListAsync(
        McpProcessDriver process,
        string id,
        string method,
        string property,
        string serverName)
    {
        await process.SendRequestAsync(id, method, new { }).ConfigureAwait(false);
        using var response = await process.ReadJsonAsync().ConfigureAwait(false);
        ExpectResult(response.RootElement, id, serverName + ": " + method);
        var result = response.RootElement.GetProperty("result");
        Require(
            result.TryGetProperty(property, out var values) &&
            values.ValueKind == JsonValueKind.Array &&
            values.GetArrayLength() > 0,
            $"{serverName}: {method} did not return a non-empty {property} array.");
    }

    private static async Task RunDeterministicFuzzAsync(
        McpProcessDriver process,
        McpServerDescriptor server,
        int serverIndex,
        IncrementalHash hash)
    {
        var random = new Random(FuzzSeed + serverIndex);
        for (var index = 0; index < FuzzCasesPerServer; index++)
        {
            var fuzzCase = BuildFuzzCase(serverIndex, index, random.Next(14));
            AppendHash(hash, server.AssemblyName, fuzzCase.Payload);
            await process.SendRawAsync(fuzzCase.Payload).ConfigureAwait(false);
            using (var response = await process.ReadJsonAsync().ConfigureAwait(false))
            {
                ExpectError(
                    response.RootElement,
                    fuzzCase.ExpectedCode,
                    fuzzCase.ExpectedId,
                    server.Name + $": generated case {index}");
            }

            if ((index + 1) % 16 == 0)
            {
                var pingId = $"fuzz-survival-{serverIndex}-{index}";
                await process.SendRequestAsync(pingId, "ping").ConfigureAwait(false);
                using var ping = await process.ReadJsonAsync().ConfigureAwait(false);
                ExpectResult(ping.RootElement, pingId, server.Name + ": generated-case survival ping");
            }
        }
    }

    private static GeneratedCase BuildFuzzCase(
        int serverIndex,
        int index,
        int variant)
    {
        var id = $"f-{serverIndex}-{index}";
        return variant switch
        {
            0 => new($"{{\"id\":\"{id}\",\"method\":\"ping\"}}", -32600, id),
            1 => new($"{{\"jsonrpc\":\"1.0\",\"id\":\"{id}\",\"method\":\"ping\"}}", -32600, id),
            2 => new($"{{\"jsonrpc\":2,\"id\":\"{id}\",\"method\":\"ping\"}}", -32600, id),
            3 => new($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\"}}", -32600, id),
            4 => new($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"method\":9}}", -32600, id),
            5 => new($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"method\":\"\"}}", -32600, id),
            6 => new(
                $"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"method\":\"{new string('m', 129)}\"}}",
                -32600,
                id),
            7 => new("{\"jsonrpc\":\"2.0\",\"id\":true,\"method\":\"ping\"}", -32600, null),
            8 => new("{\"jsonrpc\":\"2.0\",\"id\":{},\"method\":\"ping\"}", -32600, null),
            9 => new("{\"jsonrpc\":\"2.0\",\"id\":[],\"method\":\"ping\"}", -32600, null),
            10 => new($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"method\":\"ping\",\"params\":[]}}", -32602, id),
            11 => new($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"method\":\"ping\",\"params\":\"x\"}}", -32602, id),
            12 => new($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"method\":\"ping\",\"params\":5}}", -32602, id),
            _ => new($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"method\":\"ping\",\"params\":null}}", -32602, id),
        };
    }

    private static object InitializeParameters(string protocolVersion, string clientName) =>
        new
        {
            protocolVersion,
            capabilities = new { },
            clientInfo = new
            {
                name = clientName,
                version = "1.0.0",
                title = "MCP Conformance Client",
            },
        };

    private static void ValidateInitialize(
        JsonElement response,
        McpServerDescriptor server,
        string expectedProtocolVersion)
    {
        ExpectResult(response, "initialize", server.Name + ": initialize");
        var result = response.GetProperty("result");
        Require(
            result.GetProperty("protocolVersion").GetString() == expectedProtocolVersion,
            $"{server.Name}: protocol version mismatch.");
        var info = result.GetProperty("serverInfo");
        Require(info.GetProperty("name").GetString() == server.Name, $"{server.Name}: server name mismatch.");
        Require(info.GetProperty("title").GetString() == server.Title, $"{server.Name}: server title mismatch.");
        Require(!string.IsNullOrWhiteSpace(info.GetProperty("version").GetString()), $"{server.Name}: version missing.");

        var capabilities = result.GetProperty("capabilities");
        var names = capabilities.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(
            names.SequenceEqual(new[] { "prompts", "resources", "tools" }),
            $"{server.Name}: capabilities are not exact.");
        Require(!capabilities.GetProperty("prompts").GetProperty("listChanged").GetBoolean(), $"{server.Name}: prompts.listChanged mismatch.");
        Require(!capabilities.GetProperty("tools").GetProperty("listChanged").GetBoolean(), $"{server.Name}: tools.listChanged mismatch.");
        var resources = capabilities.GetProperty("resources");
        Require(!resources.GetProperty("subscribe").GetBoolean(), $"{server.Name}: resources.subscribe mismatch.");
        Require(!resources.GetProperty("listChanged").GetBoolean(), $"{server.Name}: resources.listChanged mismatch.");
    }

    private static void ExpectResult(
        JsonElement response,
        string expectedId,
        string context)
    {
        Require(response.GetProperty("jsonrpc").GetString() == "2.0", context + ": jsonrpc mismatch.");
        Require(response.GetProperty("id").GetString() == expectedId, context + ": id mismatch.");
        Require(response.TryGetProperty("result", out _), context + ": result missing.");
        Require(!response.TryGetProperty("error", out _), context + ": unexpected error.");
    }

    private static void ExpectError(
        JsonElement response,
        int expectedCode,
        string? expectedId,
        string context)
    {
        Require(response.GetProperty("jsonrpc").GetString() == "2.0", context + ": jsonrpc mismatch.");
        var id = response.GetProperty("id");
        if (expectedId is null)
        {
            Require(id.ValueKind == JsonValueKind.Null, context + ": expected null id.");
        }
        else
        {
            Require(id.ValueKind == JsonValueKind.String, context + ": expected string id.");
            Require(id.GetString() == expectedId, context + ": error id mismatch.");
        }

        var error = response.GetProperty("error");
        Require(error.GetProperty("code").GetInt32() == expectedCode, context + ": error code mismatch.");
        Require(!string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()), context + ": error message missing.");
        Require(!response.TryGetProperty("result", out _), context + ": error response contained result.");
    }

    private static void ValidateSafeStderr(
        string serverName,
        string stderr,
        string workspaceRoot)
    {
        foreach (var forbidden in new[]
                 {
                     Canary,
                     workspaceRoot,
                     ".bookstudio",
                     "bookstudio.db",
                     "Data Source",
                 })
        {
            Require(
                !stderr.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"{serverName}: stderr leaked forbidden content.");
        }

        foreach (var line in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            Require(line.Length <= 96, $"{serverName}: stderr diagnostic exceeded 96 characters.");
            Require(
                line.All(character =>
                    char.IsAsciiLetterOrDigit(character) || character is '_' or '-'),
                $"{serverName}: stderr contained non-diagnostic content.");
        }
    }

    private static McpConformanceCorpus LoadCorpus()
    {
        using var stream = typeof(McpConformanceRunner).Assembly
            .GetManifestResourceStream("mcp-conformance-v1.json")
            ?? throw new InvalidOperationException("Embedded MCP conformance corpus is missing.");
        return JsonSerializer.Deserialize<McpConformanceCorpus>(stream, JsonOptions)
               ?? throw new InvalidOperationException("MCP conformance corpus is empty.");
    }

    private static void ValidateCorpus(McpConformanceCorpus corpus)
    {
        Require(corpus.SchemaVersion == "1.0.0", "Unsupported conformance corpus schema.");
        Require(corpus.ProtocolVersion == "2025-11-25", "Unexpected conformance protocol version.");
        Require(corpus.Cases.Count >= 18, "Conformance corpus is too small.");
        Require(
            corpus.Cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == corpus.Cases.Count,
            "Conformance corpus contains duplicate IDs.");
        Require(
            corpus.Cases.Select(item => item.Phase).ToHashSet(StringComparer.Ordinal)
                .SetEquals(["created", "ready"]),
            "Conformance corpus phases are invalid.");

        var allowedCodes = new HashSet<int> { -32700, -32600, -32602, -32601, -32002 };
        foreach (var item in corpus.Cases)
        {
            Require(!string.IsNullOrWhiteSpace(item.Id) && item.Id.Length <= 96, "Corpus case ID is invalid.");
            Require(allowedCodes.Contains(item.ExpectedCode), $"Corpus case {item.Id} has an unsupported code.");
            Require(
                Encoding.UTF8.GetByteCount(item.Payload) <= StdioJsonRpcServer.MaximumMessageBytes,
                $"Corpus case {item.Id} exceeds the normal message limit.");
            Require(
                item.ExpectedId is null ||
                item.ExpectedId.Length <= 128 && item.ExpectedId.All(character => !char.IsControl(character)),
                $"Corpus case {item.Id} has an invalid expected ID.");
        }
    }

    private static void AppendHash(
        IncrementalHash hash,
        string assemblyName,
        string payload)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(assemblyName));
        hash.AppendData([(byte)'\n']);
        hash.AppendData(Encoding.UTF8.GetBytes(payload));
        hash.AppendData([(byte)'\n']);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void TryDelete(string path)
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

    private sealed record McpServerDescriptor(
        string AssemblyName,
        string Name,
        string Title);

    private sealed record GeneratedCase(
        string Payload,
        int ExpectedCode,
        string? ExpectedId);
}

internal sealed record McpConformanceCorpus(
    string SchemaVersion,
    string ProtocolVersion,
    IReadOnlyList<McpConformanceCase> Cases);

internal sealed record McpConformanceCase(
    string Id,
    string Phase,
    string Payload,
    int ExpectedCode,
    string? ExpectedId);

internal sealed record McpConformanceReport(
    int Servers,
    int CorpusCases,
    int FuzzCases,
    int Seed,
    string Sha256);
