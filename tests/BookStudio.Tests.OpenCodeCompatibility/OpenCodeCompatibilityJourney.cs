using System.Text;
using System.Text.Json;
using BookStudio.Application.OpenCode;
using BookStudio.OpenCode;

namespace BookStudio.Tests.OpenCodeCompatibility;

internal sealed class OpenCodeCompatibilityJourney
{
    private static readonly IReadOnlyDictionary<string, Operation> Operations =
        new Dictionary<string, Operation>(StringComparer.Ordinal)
        {
            [OpenCodeFeatureIds.ProvidersList] = new("/provider", "get"),
            [OpenCodeFeatureIds.AgentsList] = new("/agent", "get"),
            [OpenCodeFeatureIds.McpStatus] = new("/mcp", "get"),
            [OpenCodeFeatureIds.SessionsList] = new("/session", "get"),
            [OpenCodeFeatureIds.SessionsCreate] = new("/session", "post"),
            [OpenCodeFeatureIds.SessionsGet] = new("/session/{sessionID}", "get"),
            [OpenCodeFeatureIds.SessionsStatus] = new("/session/status", "get"),
            [OpenCodeFeatureIds.SessionsPromptAsync] = new("/session/{sessionID}/prompt_async", "post"),
            [OpenCodeFeatureIds.SessionsAbort] = new("/session/{sessionID}/abort", "post"),
            [OpenCodeFeatureIds.EventsProject] = new("/event", "get"),
            [OpenCodeFeatureIds.EventsGlobal] = new("/global/event", "get"),
        };

    private int _scenarioCount;
    private int _requestCount;

    public async Task<OpenCodeCompatibilityJourneyReport> RunAsync()
    {
        await CompatibleServerAsync().ConfigureAwait(false);
        await MissingFeatureAsync().ConfigureAwait(false);
        await UnhealthyServerAsync().ConfigureAwait(false);
        await AuthenticationRequiredAsync().ConfigureAwait(false);
        await BasicAuthenticationAsync().ConfigureAwait(false);
        await MalformedHealthAsync().ConfigureAwait(false);
        await InvalidOpenApiAsync().ConfigureAwait(false);
        await HtmlDocumentationAsync().ConfigureAwait(false);
        await OversizedHealthAsync().ConfigureAwait(false);
        await OversizedSpecificationAsync().ConfigureAwait(false);
        await TimeoutAsync().ConfigureAwait(false);
        await ExternalCancellationAsync().ConfigureAwait(false);
        EndpointValidation();

        return new OpenCodeCompatibilityJourneyReport(
            _scenarioCount,
            _requestCount,
            OpenCodeFeatureIds.Required.Count);
    }

    private async Task CompatibleServerAsync()
    {
        await using var server = CreateServer(healthy: true, openApi: BuildOpenApi());
        var report = await ProbeAsync(server).ConfigureAwait(false);
        Require(report.State == OpenCodeCompatibilityStates.Compatible, "Compatible server was not accepted.");
        Require(report.Code == "compatible", "Compatible server code drifted.");
        Require(report.ServerVersion == "1.2.3", "OpenCode version was not preserved.");
        Require(report.DetectedFeatures.SequenceEqual(OpenCodeFeatureIds.Required), "Detected feature matrix drifted.");
        Require(report.MissingRequiredFeatures.Count == 0, "Compatible server reported missing features.");
        Require(report.Facts["requests"] == "2", "Compatible probe request count drifted.");
        Require(report.Facts["healthy"] == "true", "Compatible health fact drifted.");
        Record(server, expectedRequests: 2);
    }

    private async Task MissingFeatureAsync()
    {
        await using var server = CreateServer(
            healthy: true,
            openApi: BuildOpenApi(OpenCodeFeatureIds.SessionsAbort));
        var report = await ProbeAsync(server).ConfigureAwait(false);
        Require(report.State == OpenCodeCompatibilityStates.Degraded, "Missing feature did not degrade compatibility.");
        Require(report.Code == "missing_required_features", "Missing feature code drifted.");
        Require(report.Facts["healthy"] == "true", "Degraded compatible-health fact drifted.");
        Require(report.MissingRequiredFeatures.SequenceEqual([OpenCodeFeatureIds.SessionsAbort]),
            "Missing feature set was not exact.");
        Require(!report.DetectedFeatures.Contains(OpenCodeFeatureIds.SessionsAbort),
            "Missing feature was reported as detected.");
        Record(server, expectedRequests: 2);
    }

    private async Task UnhealthyServerAsync()
    {
        await using var server = CreateServer(healthy: false, openApi: BuildOpenApi());
        var report = await ProbeAsync(server).ConfigureAwait(false);
        Require(report.State == OpenCodeCompatibilityStates.Unhealthy, "Unhealthy server state drifted.");
        Require(report.Code == "server_unhealthy", "Unhealthy server code drifted.");
        Require(report.Facts["healthy"] == "false", "Unhealthy fact drifted.");
        Require(report.DetectedFeatures.SequenceEqual([OpenCodeFeatureIds.Health]),
            "Unhealthy server feature evidence drifted.");
        Record(server, expectedRequests: 1);
    }

    private async Task AuthenticationRequiredAsync()
    {
        const string expectedAuthorization = "Basic dXNlcjpzZWNyZXQ=";
        await using var server = CreateAuthenticatedServer(expectedAuthorization);
        var report = await ProbeAsync(server).ConfigureAwait(false);
        Require(report.State == OpenCodeCompatibilityStates.AuthenticationRequired,
            "Unauthorized server state drifted.");
        Require(report.Code == "authentication_required", "Unauthorized code drifted.");
        Require(report.Facts["healthy"] == "unknown", "Pre-health authentication fact must be unknown.");
        Record(server, expectedRequests: 1);
    }

    private async Task BasicAuthenticationAsync()
    {
        const string username = "user";
        const string password = "secret";
        const string expectedAuthorization = "Basic dXNlcjpzZWNyZXQ=";
        await using var server = CreateAuthenticatedServer(expectedAuthorization);
        var options = OpenCodeEndpointOptions.Create(
            server.BaseUrl,
            username,
            password,
            requestTimeout: TimeSpan.FromSeconds(2));
        var report = await ProbeAsync(options).ConfigureAwait(false);
        Require(report.IsCompatible, "Authenticated server was not compatible.");
        Require(server.Requests.Count == 2, "Authenticated probe request count drifted.");
        Require(server.Requests.All(request =>
                request.Headers.TryGetValue("Authorization", out var authorization) &&
                authorization == expectedAuthorization),
            "Basic Authorization header was missing or incorrect.");
        var serialized = JsonSerializer.Serialize(report);
        Require(!serialized.Contains(username, StringComparison.Ordinal), "Compatibility report leaked username.");
        Require(!serialized.Contains(password, StringComparison.Ordinal), "Compatibility report leaked password.");
        Record(server, expectedRequests: 2);
    }

    private async Task MalformedHealthAsync()
    {
        await using var server = new ContractualOpenCodeServer((request, _) =>
            ValueTask.FromResult(
                request.Path == "/global/health"
                    ? ContractualResponse.Json(200, "{"u8.ToArray())
                    : ContractualResponse.Text(404, "missing")));
        var report = await ProbeAsync(server).ConfigureAwait(false);
        Require(report.State == OpenCodeCompatibilityStates.Unavailable, "Malformed health was not unavailable.");
        Require(report.Code == "health_payload_invalid", "Malformed health code drifted.");
        Require(report.Facts["healthy"] == "unknown", "Malformed health fact must be unknown.");
        Record(server, expectedRequests: 1);
    }

    private async Task InvalidOpenApiAsync()
    {
        var invalid = JsonSerializer.SerializeToUtf8Bytes(new
        {
            openapi = "2.0",
            paths = new Dictionary<string, object>(),
        });
        await using var server = CreateServer(healthy: true, openApi: invalid);
        var report = await ProbeAsync(server).ConfigureAwait(false);
        Require(report.State == OpenCodeCompatibilityStates.Degraded, "Invalid OpenAPI did not degrade.");
        Require(report.Code == "openapi_document_invalid", "Invalid OpenAPI code drifted.");
        Require(report.Facts["healthy"] == "true", "Post-health OpenAPI failure fact drifted.");
        Record(server, expectedRequests: 2);
    }

    private async Task HtmlDocumentationAsync()
    {
        await using var server = new ContractualOpenCodeServer((request, _) =>
        {
            if (request.Path == "/global/health")
            {
                return ValueTask.FromResult(ContractualResponse.Json(200, BuildHealth(true)));
            }
            if (request.Path == "/doc")
            {
                return ValueTask.FromResult(ContractualResponse.Text(200, "<html>docs</html>"));
            }
            return ValueTask.FromResult(ContractualResponse.Text(404, "missing"));
        });
        var report = await ProbeAsync(server).ConfigureAwait(false);
        Require(report.State == OpenCodeCompatibilityStates.Degraded, "HTML documentation did not degrade.");
        Require(report.Code == "openapi_document_unavailable", "HTML documentation code drifted.");
        Record(server, expectedRequests: 2);
    }

    private async Task OversizedHealthAsync()
    {
        var oversized = Enumerable.Repeat((byte)'x', 257).ToArray();
        await using var server = new ContractualOpenCodeServer((request, _) =>
            ValueTask.FromResult(
                request.Path == "/global/health"
                    ? ContractualResponse.Json(200, oversized)
                    : ContractualResponse.Text(404, "missing")));
        var options = OpenCodeEndpointOptions.Create(
            server.BaseUrl,
            requestTimeout: TimeSpan.FromSeconds(2),
            maximumHealthBytes: 256);
        var report = await ProbeAsync(options).ConfigureAwait(false);
        Require(report.State == OpenCodeCompatibilityStates.Unavailable, "Oversized health was not unavailable.");
        Require(report.Code == "response_too_large", "Oversized health code drifted.");
        Record(server, expectedRequests: 1);
    }

    private async Task OversizedSpecificationAsync()
    {
        var oversized = Enumerable.Repeat((byte)'x', 1025).ToArray();
        await using var server = CreateServer(healthy: true, openApi: oversized);
        var options = OpenCodeEndpointOptions.Create(
            server.BaseUrl,
            requestTimeout: TimeSpan.FromSeconds(2),
            maximumSpecificationBytes: 1024);
        var report = await ProbeAsync(options).ConfigureAwait(false);
        Require(report.State == OpenCodeCompatibilityStates.Degraded, "Oversized spec was not degraded.");
        Require(report.Code == "openapi_response_too_large", "Oversized spec code drifted.");
        Record(server, expectedRequests: 2);
    }

    private async Task TimeoutAsync()
    {
        await using var server = new ContractualOpenCodeServer((request, _) =>
            ValueTask.FromResult(
                request.Path == "/global/health"
                    ? ContractualResponse.Json(
                        200,
                        BuildHealth(true),
                        TimeSpan.FromMilliseconds(500))
                    : ContractualResponse.Text(404, "missing")));
        var options = OpenCodeEndpointOptions.Create(
            server.BaseUrl,
            requestTimeout: TimeSpan.FromMilliseconds(100));
        var report = await ProbeAsync(options).ConfigureAwait(false);
        Require(report.State == OpenCodeCompatibilityStates.Unavailable, "Timeout was not unavailable.");
        Require(report.Code == "request_timeout", "Timeout code drifted.");
        Require(report.Facts["healthy"] == "unknown", "Pre-health timeout fact must be unknown.");
        Record(server, expectedRequests: 1);
    }

    private async Task ExternalCancellationAsync()
    {
        await using var server = new ContractualOpenCodeServer((request, _) =>
            ValueTask.FromResult(
                request.Path == "/global/health"
                    ? ContractualResponse.Json(
                        200,
                        BuildHealth(true),
                        TimeSpan.FromSeconds(2))
                    : ContractualResponse.Text(404, "missing")));
        var options = OpenCodeEndpointOptions.Create(
            server.BaseUrl,
            requestTimeout: TimeSpan.FromSeconds(5));
        await using var probe = OpenCodeCompatibilityProbe.Create(options);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await ExpectCanceledAsync(() => probe.ProbeAsync(cancellation.Token).AsTask()).ConfigureAwait(false);
        Record(server, expectedRequests: 1);
    }

    private void EndpointValidation()
    {
        Expect<ArgumentException>(() => OpenCodeEndpointOptions.Create("http://example.com/"));
        Expect<ArgumentException>(() => OpenCodeEndpointOptions.Create("ftp://127.0.0.1/"));
        Expect<ArgumentException>(() => OpenCodeEndpointOptions.Create("http://user:secret@127.0.0.1/"));
        Expect<ArgumentException>(() => OpenCodeEndpointOptions.Create("http://127.0.0.1/path"));
        Expect<ArgumentException>(() => OpenCodeEndpointOptions.Create("http://127.0.0.1/?query=1"));
        Expect<ArgumentException>(() => OpenCodeEndpointOptions.Create("http://127.0.0.1/#fragment"));
        Expect<ArgumentException>(() => OpenCodeEndpointOptions.Create("http://127.0.0.1/", password: "secret"));
        Expect<ArgumentOutOfRangeException>(() => OpenCodeEndpointOptions.Create(
            "http://127.0.0.1/",
            requestTimeout: TimeSpan.FromMilliseconds(50)));
        Expect<ArgumentOutOfRangeException>(() => OpenCodeEndpointOptions.Create(
            "http://127.0.0.1/",
            maximumHealthBytes: 255));
        Expect<ArgumentOutOfRangeException>(() => OpenCodeEndpointOptions.Create(
            "http://127.0.0.1/",
            maximumSpecificationBytes: 4 * 1024 * 1024 + 1));
        _scenarioCount++;
    }

    private static ContractualOpenCodeServer CreateServer(bool healthy, byte[] openApi) =>
        new((request, _) =>
        {
            if (request.Path == "/global/health")
            {
                return ValueTask.FromResult(ContractualResponse.Json(200, BuildHealth(healthy)));
            }
            if (request.Path == "/doc")
            {
                return ValueTask.FromResult(ContractualResponse.Json(200, openApi));
            }
            return ValueTask.FromResult(ContractualResponse.Text(404, "missing"));
        });

    private static ContractualOpenCodeServer CreateAuthenticatedServer(string expectedAuthorization) =>
        new((request, _) =>
        {
            if (!request.Headers.TryGetValue("Authorization", out var authorization) ||
                authorization != expectedAuthorization)
            {
                return ValueTask.FromResult(ContractualResponse.Text(401, "auth required"));
            }
            if (request.Path == "/global/health")
            {
                return ValueTask.FromResult(ContractualResponse.Json(200, BuildHealth(true)));
            }
            if (request.Path == "/doc")
            {
                return ValueTask.FromResult(ContractualResponse.Json(200, BuildOpenApi()));
            }
            return ValueTask.FromResult(ContractualResponse.Text(404, "missing"));
        });

    private static async Task<OpenCodeCompatibilityReport> ProbeAsync(
        ContractualOpenCodeServer server)
    {
        var options = OpenCodeEndpointOptions.Create(
            server.BaseUrl,
            requestTimeout: TimeSpan.FromSeconds(2));
        return await ProbeAsync(options).ConfigureAwait(false);
    }

    private static async Task<OpenCodeCompatibilityReport> ProbeAsync(
        OpenCodeEndpointOptions options)
    {
        await using var probe = OpenCodeCompatibilityProbe.Create(options);
        return await probe.ProbeAsync().ConfigureAwait(false);
    }

    private void Record(ContractualOpenCodeServer server, int expectedRequests)
    {
        Require(server.Requests.Count == expectedRequests, "Probe emitted an unexpected number of HTTP requests.");
        Require(server.Requests.All(request => request.Method == "GET"),
            "Compatibility discovery emitted a non-GET request.");
        Require(server.Requests.All(request => request.Path is "/global/health" or "/doc"),
            "Compatibility discovery called an unexpected endpoint.");
        _scenarioCount++;
        _requestCount += server.Requests.Count;
    }

    private static byte[] BuildHealth(bool healthy) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            healthy,
            version = "1.2.3",
        });

    private static byte[] BuildOpenApi(params string[] omittedFeatures)
    {
        var omitted = omittedFeatures.ToHashSet(StringComparer.Ordinal);
        var paths = new SortedDictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        foreach (var (featureId, operation) in Operations)
        {
            if (omitted.Contains(featureId))
            {
                continue;
            }
            if (!paths.TryGetValue(operation.Path, out var methods))
            {
                methods = new Dictionary<string, object>(StringComparer.Ordinal);
                paths.Add(operation.Path, methods);
            }
            methods[operation.Method] = new { responses = new { } };
        }
        paths["/global/health"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["get"] = new { responses = new { } },
        };
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            openapi = "3.1.0",
            info = new { title = "Contractual OpenCode", version = "1.2.3" },
            paths,
        });
    }

    private static void Expect<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static async Task ExpectCanceledAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException("Expected cancellation.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record Operation(string Path, string Method);
}

internal sealed record OpenCodeCompatibilityJourneyReport(
    int Scenarios,
    int Requests,
    int Features);
