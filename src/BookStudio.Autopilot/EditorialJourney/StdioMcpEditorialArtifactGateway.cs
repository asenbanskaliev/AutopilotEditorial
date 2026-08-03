using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BookStudio.Autopilot.EditorialJourney;

public sealed record StdioMcpEditorialGatewayOptions(
    string WorkingDirectory,
    string WorkspaceRoot,
    string AuthoringExecutable,
    string QualityExecutable,
    string ProductionExecutable,
    TimeSpan RequestTimeout);

public sealed class StdioMcpEditorialArtifactGateway : IEditorialArtifactGateway, IAsyncDisposable
{
    private readonly StdioMcpEditorialGatewayOptions _options;
    private readonly SqliteConnection _receipts;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StdioMcpEditorialArtifactGateway(StdioMcpEditorialGatewayOptions options, string receiptConnectionString)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptConnectionString);
        SQLitePCL.Batteries_V2.Init();
        _receipts = new SqliteConnection(receiptConnectionString);
        _receipts.Open();
        using var command = _receipts.CreateCommand();
        command.CommandText = """
CREATE TABLE IF NOT EXISTS editorial_artifact_receipt (
 project_id TEXT NOT NULL,
 artifact_id TEXT NOT NULL,
 version INTEGER NOT NULL,
 sha256 TEXT NOT NULL,
 media_type TEXT NOT NULL,
 length INTEGER NOT NULL,
 PRIMARY KEY(project_id, artifact_id, version)
);
CREATE TABLE IF NOT EXISTS editorial_release_receipt (
 project_id TEXT NOT NULL,
 artifact_id TEXT NOT NULL,
 version INTEGER NOT NULL,
 sha256 TEXT NOT NULL,
 PRIMARY KEY(project_id, artifact_id, version)
);
""";
        command.ExecuteNonQuery();
    }

    public async ValueTask<PersistedEditorialArtifact?> GetAsync(
        string projectId,
        string artifactId,
        int version,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _receipts.CreateCommand();
            command.CommandText = """
SELECT sha256, media_type, length
FROM editorial_artifact_receipt
WHERE project_id=$projectId AND artifact_id=$artifactId AND version=$version
""";
            command.Parameters.AddWithValue("$projectId", projectId);
            command.Parameters.AddWithValue("$artifactId", artifactId);
            command.Parameters.AddWithValue("$version", version);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? new PersistedEditorialArtifact(artifactId, version, reader.GetString(0), reader.GetString(1), reader.GetInt64(2))
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<PersistedEditorialArtifact> RegisterAsync(
        string projectId,
        string artifactId,
        int expectedVersion,
        string mediaType,
        string content,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(projectId, artifactId, expectedVersion, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        await using var session = await McpStdioSession.StartAsync(
            _options.AuthoringExecutable,
            _options.WorkingDirectory,
            _options.WorkspaceRoot,
            _options.RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        var result = await session.CallToolAsync(
            "book.draft.register",
            new
            {
                projectId,
                payload = new { artifactId, expectedVersion, mediaType, content },
            },
            cancellationToken).ConfigureAwait(false);
        RequireReference(result, artifactId, "draft registration");

        var bytes = Encoding.UTF8.GetBytes(content);
        var receipt = new PersistedEditorialArtifact(
            artifactId,
            expectedVersion,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            mediaType,
            bytes.LongLength);
        await SaveArtifactReceiptAsync(projectId, receipt, cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    public async ValueTask<EditorialValidationResult> ValidateAsync(
        string projectId,
        string artifactId,
        int version,
        CancellationToken cancellationToken)
    {
        await using var authoring = await McpStdioSession.StartAsync(
            _options.AuthoringExecutable,
            _options.WorkingDirectory,
            _options.WorkspaceRoot,
            _options.RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        var authoringResult = await authoring.CallToolAsync(
            "book.draft.validate",
            new { projectId, payload = new { artifactId, version, maximumLineLength = 160 } },
            cancellationToken).ConfigureAwait(false);
        RequireReference(authoringResult, artifactId, "draft validation");

        var warnings = new List<string>();
        var qualityPassed = true;
        if (!string.IsNullOrWhiteSpace(_options.QualityExecutable))
        {
            await using var quality = await McpStdioSession.StartAsync(
                _options.QualityExecutable,
                _options.WorkingDirectory,
                _options.WorkspaceRoot,
                _options.RequestTimeout,
                cancellationToken).ConfigureAwait(false);
            var listed = await quality.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            var qualityTool = listed.FirstOrDefault(name =>
                name.Contains("quality", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("continuity", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("review", StringComparison.OrdinalIgnoreCase));
            if (qualityTool is not null)
            {
                var qualityResult = await quality.CallToolAsync(
                    qualityTool,
                    new { projectId, payload = new { artifactId, version } },
                    cancellationToken).ConfigureAwait(false);
                var raw = qualityResult.GetRawText();
                qualityPassed = !raw.Contains("BLOCK", StringComparison.OrdinalIgnoreCase) &&
                                !raw.Contains("FAIL", StringComparison.OrdinalIgnoreCase);
                if (!qualityPassed)
                {
                    warnings.Add("quality_mcp_blocked");
                }
            }
            else
            {
                warnings.Add("quality_tool_not_exposed");
            }
        }

        return new EditorialValidationResult(
            qualityPassed,
            warnings,
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["authoringValidationBytes"] = authoringResult.GetRawText().Length,
            });
    }

    public async ValueTask<EditorialReleaseResult?> GetReleaseAsync(
        string projectId,
        string releaseArtifactId,
        int version,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _receipts.CreateCommand();
            command.CommandText = """
SELECT sha256 FROM editorial_release_receipt
WHERE project_id=$projectId AND artifact_id=$artifactId AND version=$version
""";
            command.Parameters.AddWithValue("$projectId", projectId);
            command.Parameters.AddWithValue("$artifactId", releaseArtifactId);
            command.Parameters.AddWithValue("$version", version);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            return value is null ? null : new EditorialReleaseResult(releaseArtifactId, version, value);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<EditorialReleaseResult> PrepareReleaseAsync(
        string projectId,
        string releaseId,
        string title,
        string language,
        PersistedEditorialArtifact manuscript,
        CancellationToken cancellationToken)
    {
        var artifactId = EditorialArtifactIdFactory.Release(projectId, releaseId);
        var existing = await GetReleaseAsync(projectId, artifactId, 1, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        await using var session = await McpStdioSession.StartAsync(
            _options.ProductionExecutable,
            _options.WorkingDirectory,
            _options.WorkspaceRoot,
            _options.RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        var result = await session.CallToolAsync(
            "book.release.prepare",
            new
            {
                projectId,
                payload = new
                {
                    releaseId,
                    expectedVersion = 1,
                    title,
                    language,
                    sources = new[] { new { role = "manuscript", artifactId = manuscript.ArtifactId, version = manuscript.Version } },
                },
            },
            cancellationToken).ConfigureAwait(false);
        RequireReference(result, artifactId, "release preparation");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.GetRawText()))).ToLowerInvariant();
        var release = new EditorialReleaseResult(artifactId, 1, hash);
        await SaveReleaseReceiptAsync(projectId, release, cancellationToken).ConfigureAwait(false);
        return release;
    }

    public async ValueTask<EditorialPreflightResult> PreflightAsync(
        string projectId,
        EditorialReleaseResult release,
        CancellationToken cancellationToken)
    {
        await using var session = await McpStdioSession.StartAsync(
            _options.ProductionExecutable,
            _options.WorkingDirectory,
            _options.WorkspaceRoot,
            _options.RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        var result = await session.CallToolAsync(
            "book.preflight.run",
            new
            {
                projectId,
                payload = new { releaseArtifactId = release.ReleaseArtifactId, version = release.Version, profile = "release-basic" },
            },
            cancellationToken).ConfigureAwait(false);
        var raw = result.GetRawText();
        var passed = raw.Contains("PASS", StringComparison.OrdinalIgnoreCase) &&
                     !raw.Contains("BLOCK", StringComparison.OrdinalIgnoreCase);
        return new EditorialPreflightResult(passed, passed ? [] : ["production_preflight_blocked"]);
    }

    private async ValueTask SaveArtifactReceiptAsync(string projectId, PersistedEditorialArtifact artifact, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _receipts.CreateCommand();
            command.CommandText = """
INSERT OR REPLACE INTO editorial_artifact_receipt(project_id, artifact_id, version, sha256, media_type, length)
VALUES($projectId,$artifactId,$version,$sha256,$mediaType,$length)
""";
            command.Parameters.AddWithValue("$projectId", projectId);
            command.Parameters.AddWithValue("$artifactId", artifact.ArtifactId);
            command.Parameters.AddWithValue("$version", artifact.Version);
            command.Parameters.AddWithValue("$sha256", artifact.Sha256);
            command.Parameters.AddWithValue("$mediaType", artifact.MediaType);
            command.Parameters.AddWithValue("$length", artifact.Length);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async ValueTask SaveReleaseReceiptAsync(string projectId, EditorialReleaseResult release, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _receipts.CreateCommand();
            command.CommandText = """
INSERT OR REPLACE INTO editorial_release_receipt(project_id, artifact_id, version, sha256)
VALUES($projectId,$artifactId,$version,$sha256)
""";
            command.Parameters.AddWithValue("$projectId", projectId);
            command.Parameters.AddWithValue("$artifactId", release.ReleaseArtifactId);
            command.Parameters.AddWithValue("$version", release.Version);
            command.Parameters.AddWithValue("$sha256", release.Sha256);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static void RequireReference(JsonElement result, string expected, string operation)
    {
        if (!result.GetRawText().Contains(expected, StringComparison.Ordinal))
        {
            throw new EditorialJourneyException(EditorialJourneyStage.Chapter, "mcp_postcondition_failed", $"The {operation} response did not reference {expected}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _receipts.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

internal sealed class McpStdioSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly TimeSpan _timeout;
    private int _nextId;

    private McpStdioSession(Process process, TimeSpan timeout)
    {
        _process = process;
        _timeout = timeout;
    }

    public static async ValueTask<McpStdioSession> StartAsync(
        string executable,
        string workingDirectory,
        string workspaceRoot,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var start = new ProcessStartInfo
        {
            FileName = executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(executable);
        }
        start.ArgumentList.Add("--workspace-root");
        start.ArgumentList.Add(workspaceRoot);
        var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("MCP process could not be started.");
        }
        var session = new McpStdioSession(process, timeout);
        var initialized = await session.RequestAsync(
            "initialize",
            new
            {
                protocolVersion = "2025-11-25",
                capabilities = new { },
                clientInfo = new { name = "BookStudio.Autopilot", version = "1.0.0" },
            },
            cancellationToken).ConfigureAwait(false);
        if (!initialized.TryGetProperty("serverInfo", out _))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("MCP initialize response omitted serverInfo.");
        }
        await session.NotifyAsync("notifications/initialized", new { }, cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async ValueTask<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var result = await RequestAsync("tools/list", new { }, cancellationToken).ConfigureAwait(false);
        if (!result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return tools.EnumerateArray()
            .Select(item => item.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToArray();
    }

    public async ValueTask<JsonElement> CallToolAsync(string name, object arguments, CancellationToken cancellationToken)
    {
        var result = await RequestAsync("tools/call", new { name, arguments }, cancellationToken).ConfigureAwait(false);
        if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException($"MCP tool {name} returned isError=true.");
        }
        return result.Clone();
    }

    private async ValueTask<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        await _process.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line is null)
            {
                var error = await _process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                throw new InvalidOperationException($"MCP process closed before responding: {error}");
            }
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
            {
                continue;
            }
            if (root.TryGetProperty("error", out var errorElement))
            {
                throw new InvalidOperationException($"MCP JSON-RPC error: {errorElement.GetRawText()}");
            }
            return root.GetProperty("result").Clone();
        }
    }

    private async ValueTask NotifyAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters });
        await _process.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _process.Kill(entireProcessTree: true);
        }
        finally
        {
            _process.Dispose();
        }
    }
}
