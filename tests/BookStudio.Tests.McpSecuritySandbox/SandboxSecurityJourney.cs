using System.Text;
using System.Text.Json;
using BookStudio.Application.Artifacts;
using BookStudio.Infrastructure.Artifacts.FileSystem;
using BookStudio.Mcp.Security;

namespace BookStudio.Tests.McpSecuritySandbox;

internal sealed class SandboxSecurityJourney
{
    private const string ProtocolVersion = "2025-11-25";
    private const string PolicyUri = "book://security/sandbox-policy";

    private static readonly string[] Assemblies =
    [
        "BookStudio.Mcp.dll",
        "BookStudio.Mcp.Authoring.dll",
        "BookStudio.Mcp.Quality.dll",
        "BookStudio.Mcp.Production.dll",
        "BookStudio.Mcp.Ops.dll",
    ];

    public async Task<SandboxSecurityReport> RunAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "bookstudio-mcp-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var invalidStarts = await VerifyInvalidHostStartsAsync(root).ConfigureAwait(false);
            var policyReads = await VerifyPolicyResourcesAsync(root).ConfigureAwait(false);
            var quotaChecks = await VerifyArtifactStoreSecurityAsync(root).ConfigureAwait(false);
            return new SandboxSecurityReport(
                Assemblies.Length,
                invalidStarts,
                policyReads,
                quotaChecks);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<int> VerifyInvalidHostStartsAsync(string root)
    {
        var invalidStarts = 0;
        var filesystemRoot = Path.GetPathRoot(root)
            ?? throw new InvalidOperationException("Temporary path has no filesystem root.");
        var existingFile = Path.Combine(root, "not-a-workspace.txt");
        await File.WriteAllTextAsync(existingFile, "not a workspace").ConfigureAwait(false);

        foreach (var assembly in Assemblies)
        {
            await AssertRejectedStartAsync(
                    assembly,
                    "--workspace-root",
                    filesystemRoot)
                .ConfigureAwait(false);
            invalidStarts++;

            await AssertRejectedStartAsync(
                    assembly,
                    "--workspace-root",
                    existingFile)
                .ConfigureAwait(false);
            invalidStarts++;

            var quotaRoot = Path.Combine(root, "invalid-quota-" + SafeAssemblyName(assembly));
            await AssertRejectedStartAsync(
                    assembly,
                    "--workspace-root",
                    quotaRoot,
                    "--max-artifact-bytes",
                    "4096",
                    "--max-store-bytes",
                    "2048")
                .ConfigureAwait(false);
            invalidStarts++;

            await AssertRejectedStartAsync(
                    assembly,
                    "--workspace-root",
                    quotaRoot,
                    "--max-store-files",
                    "00016")
                .ConfigureAwait(false);
            invalidStarts++;
        }

        var linkParent = Path.Combine(root, "link-parent");
        var linkTarget = Path.Combine(root, "link-target");
        Directory.CreateDirectory(linkTarget);
        if (TryCreateDirectorySymbolicLink(linkParent, linkTarget))
        {
            foreach (var assembly in Assemblies)
            {
                await AssertRejectedStartAsync(
                        assembly,
                        "--workspace-root",
                        Path.Combine(linkParent, "nested"))
                    .ConfigureAwait(false);
                invalidStarts++;
            }
        }

        return invalidStarts;
    }

    private static async Task AssertRejectedStartAsync(
        string assembly,
        params string[] arguments)
    {
        var completion = await SandboxProcessDriver
            .RunToExitAsync(assembly, arguments)
            .ConfigureAwait(false);
        Require(completion.ExitCode == 2, "Unsafe MCP host start did not return exit code 2.");
        Require(
            string.IsNullOrEmpty(completion.RemainingStdout),
            "Unsafe MCP host start wrote to stdout.");
        Require(
            string.Equals(
                completion.Stderr.Trim(),
                "MCP_INVALID_HOST_OPTIONS",
                StringComparison.Ordinal),
            "Unsafe MCP host start did not emit the bounded diagnostic.");
    }

    private static async Task<int> VerifyPolicyResourcesAsync(string root)
    {
        var reads = 0;
        foreach (var assembly in Assemblies)
        {
            var workspace = Path.Combine(root, "policy-" + SafeAssemblyName(assembly));
            await using var process = SandboxProcessDriver.Start(
                assembly,
                "--workspace-root",
                workspace,
                "--max-artifact-bytes",
                "4096",
                "--max-store-bytes",
                "131072",
                "--max-store-files",
                "32");

            await process.SendRequestAsync(
                    "initialize",
                    "initialize",
                    new
                    {
                        protocolVersion = ProtocolVersion,
                        capabilities = new { },
                        clientInfo = new
                        {
                            name = "bookstudio-security-tests",
                            version = "1.0.0",
                            title = "BookStudio Security Tests",
                        },
                    })
                .ConfigureAwait(false);
            using (var initialize = await process.ReadJsonAsync().ConfigureAwait(false))
            {
                var result = initialize.RootElement.GetProperty("result");
                Require(
                    result.GetProperty("protocolVersion").GetString() == ProtocolVersion,
                    "MCP protocol version was not negotiated exactly.");
            }

            await process.SendNotificationAsync("notifications/initialized", new { })
                .ConfigureAwait(false);

            var resources = await ReadAllResourceUrisAsync(process).ConfigureAwait(false);
            Require(resources.Contains(PolicyUri, StringComparer.Ordinal), "Sandbox policy resource is missing.");
            Require(
                resources.Count == resources.Distinct(StringComparer.Ordinal).Count(),
                "Sandbox resource catalog contains duplicate URIs.");

            await process.SendRequestAsync(
                    "policy-read",
                    "resources/read",
                    new { uri = PolicyUri })
                .ConfigureAwait(false);
            using (var response = await process.ReadJsonAsync().ConfigureAwait(false))
            {
                var contents = response.RootElement
                    .GetProperty("result")
                    .GetProperty("contents");
                Require(contents.GetArrayLength() == 1, "Sandbox policy read returned an invalid content count.");
                var content = contents[0];
                Require(content.GetProperty("uri").GetString() == PolicyUri, "Sandbox policy URI drifted.");
                var text = content.GetProperty("text").GetString()
                    ?? throw new InvalidOperationException("Sandbox policy text is missing.");
                Require(
                    !text.Contains(workspace, StringComparison.OrdinalIgnoreCase),
                    "Sandbox policy leaked the workspace root.");
                using var policy = JsonDocument.Parse(text);
                var policyRoot = policy.RootElement;
                Require(policyRoot.GetProperty("mode").GetString() == "strict-local", "Sandbox mode drifted.");
                Require(policyRoot.GetProperty("maximumArtifactBytes").GetInt64() == 4096, "Artifact limit drifted.");
                Require(policyRoot.GetProperty("maximumStoreBytes").GetInt64() == 131072, "Store byte limit drifted.");
                Require(policyRoot.GetProperty("maximumStoreFiles").GetInt32() == 32, "Store file limit drifted.");
            }

            Require(!Directory.Exists(workspace), "Reading static sandbox policy activated the workspace.");
            var completion = await process.CloseAsync().ConfigureAwait(false);
            Require(completion.ExitCode == 0, "MCP sandbox target failed to exit cleanly.");
            Require(string.IsNullOrEmpty(completion.RemainingStdout), "MCP sandbox target left stdout content after EOF.");
            Require(string.IsNullOrEmpty(completion.Stderr), "MCP sandbox target wrote unexpected stderr.");
            reads++;
        }

        return reads;
    }

    private static async Task<IReadOnlyList<string>> ReadAllResourceUrisAsync(
        SandboxProcessDriver process)
    {
        var resources = new List<string>();
        string? cursor = null;
        var requestNumber = 0;
        do
        {
            requestNumber++;
            if (cursor is null)
            {
                await process.SendRequestAsync(
                        "resources-" + requestNumber,
                        "resources/list")
                    .ConfigureAwait(false);
            }
            else
            {
                await process.SendRequestAsync(
                        "resources-" + requestNumber,
                        "resources/list",
                        new { cursor })
                    .ConfigureAwait(false);
            }

            using var response = await process.ReadJsonAsync().ConfigureAwait(false);
            var result = response.RootElement.GetProperty("result");
            foreach (var resource in result.GetProperty("resources").EnumerateArray())
            {
                resources.Add(
                    resource.GetProperty("uri").GetString()
                    ?? throw new InvalidOperationException("Resource URI is missing."));
            }
            cursor = result.TryGetProperty("nextCursor", out var next)
                ? next.GetString()
                : null;
            Require(requestNumber <= 20, "Resource pagination did not terminate.");
        }
        while (cursor is not null);

        return resources;
    }

    private static async Task<int> VerifyArtifactStoreSecurityAsync(string root)
    {
        var checks = 0;
        await VerifyArtifactLimitAsync(Path.Combine(root, "artifact-limit")).ConfigureAwait(false);
        checks++;
        await VerifyTraversalRejectedAsync(Path.Combine(root, "traversal")).ConfigureAwait(false);
        checks++;
        await VerifyFileQuotaProjectionAsync(Path.Combine(root, "file-quota")).ConfigureAwait(false);
        checks++;
        await VerifyByteQuotaAndVersionPreservationAsync(Path.Combine(root, "byte-quota")).ConfigureAwait(false);
        checks++;
        await VerifyDeduplicatedFileQuotaAsync(Path.Combine(root, "dedupe-quota")).ConfigureAwait(false);
        checks++;
        return checks;
    }

    private static async Task VerifyArtifactLimitAsync(string workspace)
    {
        await using var store = new FileArtifactStore(
            FileArtifactStoreOptions.Create(
                workspace,
                maximumArtifactBytes: 8,
                maximumStoreBytes: 1024 * 1024,
                maximumStoreFiles: 100,
                bufferSize: 4096));
        await ExpectAsync<ArtifactSizeLimitExceededException>(
                () => PutAsync(store, "limit-item", 1, new byte[9]))
            .ConfigureAwait(false);
        Require(!EnumerateFiles(workspace).Any(), "Rejected oversized artifact left files behind.");
    }

    private static async Task VerifyTraversalRejectedAsync(string workspace)
    {
        await using var store = new FileArtifactStore(
            FileArtifactStoreOptions.Create(
                workspace,
                maximumArtifactBytes: 1024,
                maximumStoreBytes: 1024 * 1024,
                maximumStoreFiles: 100,
                bufferSize: 4096));
        await ExpectAsync<ArgumentException>(
                () => PutAsync(store, "../escape", 1, [1]))
            .ConfigureAwait(false);
        Require(!File.Exists(Path.Combine(workspace, "escape")), "Traversal created a file outside the store.");
        Require(!EnumerateFiles(workspace).Any(), "Rejected traversal left files behind.");
    }

    private static async Task VerifyFileQuotaProjectionAsync(string workspace)
    {
        await using var store = new FileArtifactStore(
            FileArtifactStoreOptions.Create(
                workspace,
                maximumArtifactBytes: 1024,
                maximumStoreBytes: 1024 * 1024,
                maximumStoreFiles: 1,
                bufferSize: 4096));
        await ExpectAsync<ArtifactStoreQuotaExceededException>(
                () => PutAsync(store, "file-quota-item", 1, [1]))
            .ConfigureAwait(false);
        Require(!EnumerateFiles(workspace).Any(), "File quota rejection published a blob or manifest.");
        Require((await store.ListVersionsAsync("file-quota-item").ConfigureAwait(false)).Count == 0,
            "File quota rejection consumed a version.");
    }

    private static async Task VerifyByteQuotaAndVersionPreservationAsync(string workspace)
    {
        const string artifactId = "byte-quota-item";
        await using (var initial = new FileArtifactStore(
                         FileArtifactStoreOptions.Create(
                             workspace,
                             maximumArtifactBytes: 1024,
                             maximumStoreBytes: 1024 * 1024,
                             maximumStoreFiles: 100,
                             bufferSize: 4096)))
        {
            _ = await PutAsync(initial, artifactId, 1, Enumerable.Repeat((byte)0x11, 256).ToArray())
                .ConfigureAwait(false);
        }

        var existingBytes = EnumerateFiles(workspace).Sum(path => new FileInfo(path).Length);
        var rejectedPayload = Enumerable.Repeat((byte)0x22, 1024).ToArray();
        await using (var constrained = new FileArtifactStore(
                         FileArtifactStoreOptions.Create(
                             workspace,
                             maximumArtifactBytes: 1024,
                             maximumStoreBytes: checked(existingBytes + rejectedPayload.Length),
                             maximumStoreFiles: 100,
                             bufferSize: 4096)))
        {
            await ExpectAsync<ArtifactStoreQuotaExceededException>(
                    () => PutAsync(constrained, artifactId, 2, rejectedPayload))
                .ConfigureAwait(false);
            var versions = await constrained.ListVersionsAsync(artifactId).ConfigureAwait(false);
            Require(versions.Select(item => item.Version).SequenceEqual([1]),
                "Byte quota rejection consumed or published version 2.");
            Require(!EnumerateTempFiles(workspace).Any(), "Byte quota rejection left temporary files.");
        }

        await using var reopened = new FileArtifactStore(
            FileArtifactStoreOptions.Create(
                workspace,
                maximumArtifactBytes: 1024,
                maximumStoreBytes: 1024 * 1024,
                maximumStoreFiles: 100,
                bufferSize: 4096));
        var accepted = await PutAsync(reopened, artifactId, 2, [0x33]).ConfigureAwait(false);
        Require(accepted.Version == 2, "Rejected write consumed the immutable version number.");
    }

    private static async Task VerifyDeduplicatedFileQuotaAsync(string workspace)
    {
        var payload = Encoding.UTF8.GetBytes("same-content");
        await using var store = new FileArtifactStore(
            FileArtifactStoreOptions.Create(
                workspace,
                maximumArtifactBytes: 1024,
                maximumStoreBytes: 1024 * 1024,
                maximumStoreFiles: 3,
                bufferSize: 4096));
        _ = await PutAsync(store, "dedupe-a", 1, payload).ConfigureAwait(false);
        _ = await PutAsync(store, "dedupe-b", 1, payload).ConfigureAwait(false);
        Require(EnumerateFiles(workspace).Count() == 3,
            "Deduplicated writes did not produce one blob and two manifests.");
        await ExpectAsync<ArtifactStoreQuotaExceededException>(
                () => PutAsync(store, "dedupe-c", 1, payload))
            .ConfigureAwait(false);
        Require((await store.ListVersionsAsync("dedupe-c").ConfigureAwait(false)).Count == 0,
            "Deduplicated quota rejection published a manifest.");
    }

    private static async Task<ArtifactManifest> PutAsync(
        FileArtifactStore store,
        string artifactId,
        int version,
        byte[] payload)
    {
        await using var content = new MemoryStream(payload, writable: false);
        return await store.PutAsync(
                new ArtifactWriteRequest(
                    artifactId,
                    version,
                    "application/octet-stream",
                    content))
            .ConfigureAwait(false);
    }

    private static async Task ExpectAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static IEnumerable<string> EnumerateFiles(string workspace) =>
        Directory.Exists(workspace)
            ? Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories)
            : [];

    private static IEnumerable<string> EnumerateTempFiles(string workspace)
    {
        var temp = Path.Combine(workspace, ".bookstudio", "artifacts", "temp");
        return Directory.Exists(temp)
            ? Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories)
            : [];
    }

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            _ = Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static string SafeAssemblyName(string assembly) =>
        assembly.Replace('.', '-').ToLowerInvariant();

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
}

internal sealed record SandboxSecurityReport(
    int Servers,
    int InvalidStarts,
    int PolicyReads,
    int QuotaChecks);
