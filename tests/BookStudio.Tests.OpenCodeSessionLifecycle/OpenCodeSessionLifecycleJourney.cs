using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using BookStudio.Application.OpenCode;
using BookStudio.OpenCode;

namespace BookStudio.Tests.OpenCodeSessionLifecycle;

internal sealed class OpenCodeSessionLifecycleJourney
{
    private const string NoUnplannedMutationMarker = "NO_UNPLANNED_MUTATION";
    private const string SuccessMarker = "SESSION_LIFECYCLE_PASS";

    private int _scenarioCount;
    private int _requestCount;
    private int _mutationCount;

    public async Task<OpenCodeSessionLifecycleJourneyReport> RunAsync()
    {
        await CompatibilityRefusalAsync().ConfigureAwait(false);
        await CreateAndGetAsync().ConfigureAwait(false);
        await CreateIdempotencyAsync().ConfigureAwait(false);
        await ConcurrentCreateIdempotencyAsync().ConfigureAwait(false);
        await PromptAsync().ConfigureAwait(false);
        await PromptIdempotencyAsync().ConfigureAwait(false);
        await StatusesAsync().ConfigureAwait(false);
        await AbortAsync().ConfigureAwait(false);
        await AuthenticationAsync().ConfigureAwait(false);
        await BoundsAsync().ConfigureAwait(false);
        await MalformedResponsesAsync().ConfigureAwait(false);
        await TimeoutAndCancellationAsync().ConfigureAwait(false);
        await FailedReservationCanRetryAsync().ConfigureAwait(false);

        return new OpenCodeSessionLifecycleJourneyReport(
            _scenarioCount,
            _requestCount,
            _mutationCount,
            NoUnplannedMutationMarker,
            SuccessMarker);
    }

    private async Task CompatibilityRefusalAsync()
    {
        await using var server = CreateServer(
            (_, _) => ValueTask.FromResult(ContractualSessionResponse.Text(500, "unexpected")),
            excludedFeature: OpenCodeFeatureIds.SessionsAbort);
        await using var lifecycle = CreateLifecycle(server);
        await ExpectCodeAsync(
                () => lifecycle.CreateSessionAsync(
                    new OpenCodeCreateSessionCommand(null, "Blocked", "compat-refusal")).AsTask(),
                OpenCodeSessionErrorCodes.OpenCodeSessionFeaturesMissing)
            .ConfigureAwait(false);
        Require(server.Requests.All(request => request.Method == "GET"),
            "Compatibility refusal emitted a mutation.");
        Record(server, expectedRequests: 2, expectedMutations: 0);
    }

    private async Task CreateAndGetAsync()
    {
        var session = BuildSession("ses_create", null, "Created", 100, 200);
        await using var server = CreateServer((request, _) =>
        {
            if (request.Method == "POST" && request.Path == "/session")
            {
                AssertCreateBody(request.Body, parentId: null, title: "Created");
                return ValueTask.FromResult(ContractualSessionResponse.Json(200, session));
            }
            if (request.Method == "GET" && request.Path == "/session/ses_create")
            {
                Require(request.Body.Length == 0, "GET session unexpectedly contained a body.");
                return ValueTask.FromResult(ContractualSessionResponse.Json(200, session));
            }
            return ValueTask.FromResult(ContractualSessionResponse.Text(404, "missing"));
        });
        await using var lifecycle = CreateLifecycle(server);
        var created = await lifecycle.CreateSessionAsync(
                new OpenCodeCreateSessionCommand(null, "Created", "create-get"))
            .ConfigureAwait(false);
        Require(created.Id == "ses_create", "Created session ID drifted.");
        Require(created.Title == "Created", "Created session title drifted.");
        Require(created.CreatedUnixMilliseconds == 100, "Created timestamp drifted.");
        Require(created.UpdatedUnixMilliseconds == 200, "Updated timestamp drifted.");
        var fetched = await lifecycle.GetSessionAsync("ses_create").ConfigureAwait(false);
        Require(fetched == created, "Fetched session projection drifted.");
        Record(server, expectedRequests: 4, expectedMutations: 1);
    }

    private async Task CreateIdempotencyAsync()
    {
        var session = BuildSession("ses_idempotent", null, "Stable", 1, 2);
        await using var server = CreateServer((request, _) =>
            ValueTask.FromResult(
                request.Method == "POST" && request.Path == "/session"
                    ? ContractualSessionResponse.Json(200, session)
                    : ContractualSessionResponse.Text(404, "missing")));
        await using var lifecycle = CreateLifecycle(server);
        var command = new OpenCodeCreateSessionCommand(null, "Stable", "create-stable");
        var first = await lifecycle.CreateSessionAsync(command).ConfigureAwait(false);
        var second = await lifecycle.CreateSessionAsync(command).ConfigureAwait(false);
        Require(first == second, "Idempotent create replay returned a different result.");
        await ExpectCodeAsync(
                () => lifecycle.CreateSessionAsync(
                    command with { Title = "Conflict" }).AsTask(),
                OpenCodeSessionErrorCodes.IdempotencyConflict)
            .ConfigureAwait(false);
        Require(Count(server, "POST", "/session") == 1,
            "Idempotent create emitted more than one provider mutation.");
        Record(server, expectedRequests: 3, expectedMutations: 1);
    }

    private async Task ConcurrentCreateIdempotencyAsync()
    {
        var session = BuildSession("ses_concurrent", null, "Concurrent", 1, 2);
        await using var server = CreateServer((request, _) =>
            ValueTask.FromResult(
                request.Method == "POST" && request.Path == "/session"
                    ? ContractualSessionResponse.Json(
                        200,
                        session,
                        TimeSpan.FromMilliseconds(200))
                    : ContractualSessionResponse.Text(404, "missing")));
        await using var lifecycle = CreateLifecycle(server);
        var command = new OpenCodeCreateSessionCommand(null, "Concurrent", "create-concurrent");
        var firstTask = lifecycle.CreateSessionAsync(command).AsTask();
        var secondTask = lifecycle.CreateSessionAsync(command).AsTask();
        var results = await Task.WhenAll(firstTask, secondTask).ConfigureAwait(false);
        Require(results[0] == results[1], "Concurrent duplicate create results drifted.");
        Require(Count(server, "POST", "/session") == 1,
            "Concurrent duplicate create was not collapsed.");
        Record(server, expectedRequests: 3, expectedMutations: 1);
    }

    private async Task PromptAsync()
    {
        await using var server = CreateServer((request, _) =>
        {
            if (request.Method == "POST" && request.Path == "/session/ses_prompt/prompt_async")
            {
                AssertPromptBody(request.Body, ["First line", "Second line"]);
                return ValueTask.FromResult(ContractualSessionResponse.NoContent());
            }
            return ValueTask.FromResult(ContractualSessionResponse.Text(404, "missing"));
        });
        await using var lifecycle = CreateLifecycle(server);
        var result = await lifecycle.SendPromptAsync(
                new OpenCodeSendPromptCommand(
                    "ses_prompt",
                    [new("First line"), new("Second line")],
                    "prompt-once"))
            .ConfigureAwait(false);
        Require(result.Accepted, "Async prompt was not accepted.");
        Require(result.SessionId == "ses_prompt", "Prompt result session drifted.");
        Record(server, expectedRequests: 3, expectedMutations: 1);
    }

    private async Task PromptIdempotencyAsync()
    {
        await using var server = CreateServer((request, _) =>
            ValueTask.FromResult(
                request.Method == "POST" && request.Path == "/session/ses_prompt_idem/prompt_async"
                    ? ContractualSessionResponse.NoContent()
                    : ContractualSessionResponse.Text(404, "missing")));
        await using var lifecycle = CreateLifecycle(server);
        var command = new OpenCodeSendPromptCommand(
            "ses_prompt_idem",
            [new("Stable prompt")],
            "prompt-stable");
        var first = await lifecycle.SendPromptAsync(command).ConfigureAwait(false);
        var second = await lifecycle.SendPromptAsync(command).ConfigureAwait(false);
        Require(first == second, "Prompt idempotent replay drifted.");
        await ExpectCodeAsync(
                () => lifecycle.SendPromptAsync(
                    command with { Parts = [new("Different prompt")] }).AsTask(),
                OpenCodeSessionErrorCodes.IdempotencyConflict)
            .ConfigureAwait(false);
        Require(Count(server, "POST", "/session/ses_prompt_idem/prompt_async") == 1,
            "Prompt idempotency emitted duplicate provider mutation.");
        Record(server, expectedRequests: 3, expectedMutations: 1);
    }

    private async Task StatusesAsync()
    {
        var statuses = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["ses_z"] = new { type = "busy" },
            ["ses_a"] = new { type = "idle" },
            ["ses_retry"] = new { type = "retry", attempt = 3, message = "backoff", next = 123456L },
            ["ses_unknown"] = new { type = "paused" },
        });
        await using var server = CreateServer((request, _) =>
            ValueTask.FromResult(
                request.Method == "GET" && request.Path == "/session/status"
                    ? ContractualSessionResponse.Json(200, statuses)
                    : ContractualSessionResponse.Text(404, "missing")));
        await using var lifecycle = CreateLifecycle(server);
        var result = await lifecycle.GetStatusesAsync().ConfigureAwait(false);
        Require(result.Keys.SequenceEqual(result.Keys.Order(StringComparer.Ordinal)),
            "Status dictionary is not ordinally sorted.");
        Require(result["ses_a"].Type == OpenCodeSessionStatusTypes.Idle, "Idle status drifted.");
        Require(result["ses_z"].Type == OpenCodeSessionStatusTypes.Busy, "Busy status drifted.");
        Require(result["ses_retry"].Type == OpenCodeSessionStatusTypes.Retry, "Retry status drifted.");
        Require(result["ses_retry"].Attempt == 3, "Retry attempt drifted.");
        Require(result["ses_retry"].Message == "backoff", "Retry message drifted.");
        Require(result["ses_retry"].NextUnixMilliseconds == 123456, "Retry next drifted.");
        Require(result["ses_unknown"].Type == OpenCodeSessionStatusTypes.Unknown,
            "Unknown provider status was not retained safely.");
        Require(result["ses_unknown"].ProviderType == "paused", "Unknown provider type drifted.");
        Record(server, expectedRequests: 3, expectedMutations: 0);
    }

    private async Task AbortAsync()
    {
        var abortCount = 0;
        await using var server = CreateServer((request, _) =>
        {
            if (request.Method == "POST" && request.Path == "/session/ses_abort/abort")
            {
                var accepted = Interlocked.Increment(ref abortCount) == 1;
                return ValueTask.FromResult(
                    ContractualSessionResponse.Json(200, JsonSerializer.SerializeToUtf8Bytes(accepted)));
            }
            return ValueTask.FromResult(ContractualSessionResponse.Text(404, "missing"));
        });
        await using var lifecycle = CreateLifecycle(server);
        var first = await lifecycle.AbortSessionAsync("ses_abort").ConfigureAwait(false);
        var second = await lifecycle.AbortSessionAsync("ses_abort").ConfigureAwait(false);
        Require(first.Accepted, "Accepted abort drifted.");
        Require(!second.Accepted, "Rejected abort was fabricated as accepted.");
        Record(server, expectedRequests: 4, expectedMutations: 2);
    }

    private async Task AuthenticationAsync()
    {
        const string username = "session-user";
        const string password = "session-secret";
        const string expected = "Basic c2Vzc2lvbi11c2VyOnNlc3Npb24tc2VjcmV0";
        var session = BuildSession("ses_auth", null, "Authenticated", 1, 1);
        await using var server = CreateServer((request, _) =>
        {
            if (!request.Headers.TryGetValue("Authorization", out var authorization) || authorization != expected)
            {
                return ValueTask.FromResult(ContractualSessionResponse.Text(401, "auth required"));
            }
            return ValueTask.FromResult(
                request.Method == "POST" && request.Path == "/session"
                    ? ContractualSessionResponse.Json(200, session)
                    : ContractualSessionResponse.Text(404, "missing"));
        }, requiredAuthorization: expected);
        var endpoint = OpenCodeEndpointOptions.Create(
            server.BaseUrl,
            username,
            password,
            requestTimeout: TimeSpan.FromSeconds(2));
        await using var lifecycle = OpenCodeSessionLifecycleClient.Create(endpoint);
        var result = await lifecycle.CreateSessionAsync(
                new OpenCodeCreateSessionCommand(null, "Authenticated", "auth-create"))
            .ConfigureAwait(false);
        Require(result.Id == "ses_auth", "Authenticated create failed.");
        Require(server.Requests.All(request =>
                request.Headers.TryGetValue("Authorization", out var authorization) &&
                authorization == expected),
            "Basic Authorization was not applied to every lifecycle request.");
        var serialized = JsonSerializer.Serialize(result);
        Require(!serialized.Contains(username, StringComparison.Ordinal), "Session result leaked username.");
        Require(!serialized.Contains(password, StringComparison.Ordinal), "Session result leaked password.");
        Record(server, expectedRequests: 3, expectedMutations: 1);
    }

    private async Task BoundsAsync()
    {
        await using var server = CreateServer((_, _) =>
            ValueTask.FromResult(ContractualSessionResponse.Text(500, "unexpected")));
        await using var lifecycle = CreateLifecycle(server);
        await ExpectArgumentAsync(() => lifecycle.GetSessionAsync("../escape").AsTask()).ConfigureAwait(false);
        await ExpectArgumentAsync(() => lifecycle.CreateSessionAsync(
            new OpenCodeCreateSessionCommand(null, " ", "bad-title")).AsTask()).ConfigureAwait(false);
        await ExpectArgumentAsync(() => lifecycle.SendPromptAsync(
            new OpenCodeSendPromptCommand("ses_valid", [], "no-parts")).AsTask()).ConfigureAwait(false);
        await ExpectArgumentAsync(() => lifecycle.SendPromptAsync(
            new OpenCodeSendPromptCommand(
                "ses_valid",
                [new(new string('x', OpenCodeSessionValidation.MaximumTextPartBytes + 1))],
                "oversized-part")).AsTask()).ConfigureAwait(false);
        Require(server.Requests.Count == 0, "Invalid inputs reached compatibility or HTTP.");

        var largeSession = BuildSession("ses_large", null, new string('x', 1500), 1, 1);
        await using var responseServer = CreateServer((request, _) =>
            ValueTask.FromResult(
                request.Method == "POST" && request.Path == "/session"
                    ? ContractualSessionResponse.Json(200, largeSession)
                    : ContractualSessionResponse.Text(404, "missing")));
        var endpoint = OpenCodeEndpointOptions.Create(responseServer.BaseUrl, requestTimeout: TimeSpan.FromSeconds(2));
        var options = OpenCodeSessionLifecycleOptions.Default with { MaximumResponseBytes = 1024 };
        await using var bounded = OpenCodeSessionLifecycleClient.Create(endpoint, options);
        await ExpectCodeAsync(
                () => bounded.CreateSessionAsync(
                    new OpenCodeCreateSessionCommand(null, "Large", "large-response")).AsTask(),
                OpenCodeSessionErrorCodes.ResponseTooLarge)
            .ConfigureAwait(false);
        Record(responseServer, expectedRequests: 3, expectedMutations: 1);
        _scenarioCount++;
    }

    private async Task MalformedResponsesAsync()
    {
        await using var sessionServer = CreateServer((request, _) =>
            ValueTask.FromResult(
                request.Method == "POST" && request.Path == "/session"
                    ? ContractualSessionResponse.Json(200, "{}"u8.ToArray())
                    : ContractualSessionResponse.Text(404, "missing")));
        await using (var lifecycle = CreateLifecycle(sessionServer))
        {
            await ExpectCodeAsync(
                    () => lifecycle.CreateSessionAsync(
                        new OpenCodeCreateSessionCommand(null, "Bad", "bad-session")).AsTask(),
                    OpenCodeSessionErrorCodes.SessionPayloadInvalid)
                .ConfigureAwait(false);
        }
        Record(sessionServer, expectedRequests: 3, expectedMutations: 1);

        await using var statusServer = CreateServer((request, _) =>
            ValueTask.FromResult(
                request.Method == "GET" && request.Path == "/session/status"
                    ? ContractualSessionResponse.Json(200, "[]"u8.ToArray())
                    : ContractualSessionResponse.Text(404, "missing")));
        await using (var lifecycle = CreateLifecycle(statusServer))
        {
            await ExpectCodeAsync(
                    () => lifecycle.GetStatusesAsync().AsTask(),
                    OpenCodeSessionErrorCodes.StatusPayloadInvalid)
                .ConfigureAwait(false);
        }
        Record(statusServer, expectedRequests: 3, expectedMutations: 0);

        await using var abortServer = CreateServer((request, _) =>
            ValueTask.FromResult(
                request.Method == "POST" && request.Path == "/session/ses_bad_abort/abort"
                    ? ContractualSessionResponse.Json(200, "{}"u8.ToArray())
                    : ContractualSessionResponse.Text(404, "missing")));
        await using (var lifecycle = CreateLifecycle(abortServer))
        {
            await ExpectCodeAsync(
                    () => lifecycle.AbortSessionAsync("ses_bad_abort").AsTask(),
                    OpenCodeSessionErrorCodes.AbortPayloadInvalid)
                .ConfigureAwait(false);
        }
        Record(abortServer, expectedRequests: 3, expectedMutations: 1);
        _scenarioCount++;
    }

    private async Task TimeoutAndCancellationAsync()
    {
        var session = BuildSession("ses_timeout", null, "Delayed", 1, 1);
        await using var timeoutServer = CreateServer((request, _) =>
            ValueTask.FromResult(
                request.Method == "POST" && request.Path == "/session"
                    ? ContractualSessionResponse.Json(200, session, TimeSpan.FromMilliseconds(500))
                    : ContractualSessionResponse.Text(404, "missing")));
        var timeoutEndpoint = OpenCodeEndpointOptions.Create(
            timeoutServer.BaseUrl,
            requestTimeout: TimeSpan.FromMilliseconds(100));
        await using (var lifecycle = OpenCodeSessionLifecycleClient.Create(timeoutEndpoint))
        {
            await ExpectCodeAsync(
                    () => lifecycle.CreateSessionAsync(
                        new OpenCodeCreateSessionCommand(null, "Delayed", "timeout-create")).AsTask(),
                    OpenCodeSessionErrorCodes.RequestTimeout)
                .ConfigureAwait(false);
        }
        Record(timeoutServer, expectedRequests: 3, expectedMutations: 1);

        await using var cancelServer = CreateServer((request, _) =>
            ValueTask.FromResult(
                request.Method == "POST" && request.Path == "/session"
                    ? ContractualSessionResponse.Json(200, session, TimeSpan.FromSeconds(2))
                    : ContractualSessionResponse.Text(404, "missing")));
        var cancelEndpoint = OpenCodeEndpointOptions.Create(
            cancelServer.BaseUrl,
            requestTimeout: TimeSpan.FromSeconds(5));
        await using (var lifecycle = OpenCodeSessionLifecycleClient.Create(cancelEndpoint))
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await ExpectCanceledAsync(() => lifecycle.CreateSessionAsync(
                    new OpenCodeCreateSessionCommand(null, "Delayed", "cancel-create"),
                    cancellation.Token).AsTask())
                .ConfigureAwait(false);
        }
        Record(cancelServer, expectedRequests: 3, expectedMutations: 1);
        _scenarioCount++;
    }

    private async Task FailedReservationCanRetryAsync()
    {
        var createCount = 0;
        var session = BuildSession("ses_retry_create", null, "Retry", 1, 1);
        await using var server = CreateServer((request, _) =>
        {
            if (request.Method == "POST" && request.Path == "/session")
            {
                return ValueTask.FromResult(
                    Interlocked.Increment(ref createCount) == 1
                        ? ContractualSessionResponse.Text(500, "failed")
                        : ContractualSessionResponse.Json(200, session));
            }
            return ValueTask.FromResult(ContractualSessionResponse.Text(404, "missing"));
        });
        await using var lifecycle = CreateLifecycle(server);
        var command = new OpenCodeCreateSessionCommand(null, "Retry", "retry-after-failure");
        await ExpectCodeAsync(
                () => lifecycle.CreateSessionAsync(command).AsTask(),
                OpenCodeSessionErrorCodes.SessionHttpStatus)
            .ConfigureAwait(false);
        var retried = await lifecycle.CreateSessionAsync(command).ConfigureAwait(false);
        Require(retried.Id == "ses_retry_create", "Failed reservation was not released for retry.");
        Require(Count(server, "POST", "/session") == 2,
            "Retry after failed reservation did not execute exactly twice.");
        Record(server, expectedRequests: 4, expectedMutations: 2);
    }

    private static ContractualOpenCodeSessionServer CreateServer(
        Func<ContractualSessionRequest, CancellationToken, ValueTask<ContractualSessionResponse>> operationHandler,
        string? excludedFeature = null,
        string? requiredAuthorization = null)
    {
        var openApi = BuildOpenApi(excludedFeature);
        return new ContractualOpenCodeSessionServer((request, cancellationToken) =>
        {
            if (requiredAuthorization is not null &&
                (!request.Headers.TryGetValue("Authorization", out var authorization) ||
                 authorization != requiredAuthorization))
            {
                return ValueTask.FromResult(ContractualSessionResponse.Text(401, "auth required"));
            }
            if (request.Method == "GET" && request.Path == "/global/health")
            {
                return ValueTask.FromResult(
                    ContractualSessionResponse.Json(
                        200,
                        JsonSerializer.SerializeToUtf8Bytes(new { healthy = true, version = "1.2.3" })));
            }
            if (request.Method == "GET" && request.Path == "/doc")
            {
                return ValueTask.FromResult(ContractualSessionResponse.Json(200, openApi));
            }
            return operationHandler(request, cancellationToken);
        });
    }

    private static OpenCodeSessionLifecycleClient CreateLifecycle(
        ContractualOpenCodeSessionServer server,
        OpenCodeSessionLifecycleOptions? options = null)
    {
        var endpoint = OpenCodeEndpointOptions.Create(
            server.BaseUrl,
            requestTimeout: TimeSpan.FromSeconds(2));
        return OpenCodeSessionLifecycleClient.Create(endpoint, options);
    }

    private static byte[] BuildOpenApi(string? excludedFeature = null)
    {
        var operations = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal)
        {
            ["/session"] = new(StringComparer.Ordinal)
            {
                ["get"] = new { },
                ["post"] = new { },
            },
            ["/session/{sessionID}"] = new(StringComparer.Ordinal)
            {
                ["get"] = new { },
            },
            ["/session/status"] = new(StringComparer.Ordinal)
            {
                ["get"] = new { },
            },
            ["/session/{sessionID}/prompt_async"] = new(StringComparer.Ordinal)
            {
                ["post"] = new { },
            },
            ["/session/{sessionID}/abort"] = new(StringComparer.Ordinal)
            {
                ["post"] = new { },
            },
        };
        if (excludedFeature == OpenCodeFeatureIds.SessionsCreate)
        {
            operations["/session"].Remove("post");
        }
        else if (excludedFeature == OpenCodeFeatureIds.SessionsGet)
        {
            operations.Remove("/session/{sessionID}");
        }
        else if (excludedFeature == OpenCodeFeatureIds.SessionsStatus)
        {
            operations.Remove("/session/status");
        }
        else if (excludedFeature == OpenCodeFeatureIds.SessionsPromptAsync)
        {
            operations.Remove("/session/{sessionID}/prompt_async");
        }
        else if (excludedFeature == OpenCodeFeatureIds.SessionsAbort)
        {
            operations.Remove("/session/{sessionID}/abort");
        }
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            openapi = "3.1.0",
            paths = operations,
        });
    }

    private static byte[] BuildSession(
        string id,
        string? parentId,
        string? title,
        long created,
        long updated) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            parentID = parentId,
            title,
            time = new { created, updated },
        });

    private static void AssertCreateBody(byte[] body, string? parentId, string? title)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (parentId is null)
        {
            Require(!root.TryGetProperty("parentID", out _), "Create request emitted null parentID.");
        }
        else
        {
            Require(root.GetProperty("parentID").GetString() == parentId, "Create parentID drifted.");
        }
        if (title is null)
        {
            Require(!root.TryGetProperty("title", out _), "Create request emitted null title.");
        }
        else
        {
            Require(root.GetProperty("title").GetString() == title, "Create title drifted.");
        }
        Require(root.EnumerateObject().All(property => property.Name is "parentID" or "title"),
            "Create request included an unplanned property.");
    }

    private static void AssertPromptBody(byte[] body, IReadOnlyList<string> expectedTexts)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Require(root.EnumerateObject().Select(property => property.Name).SequenceEqual(["parts"]),
            "Prompt request included an unplanned property.");
        var parts = root.GetProperty("parts").EnumerateArray().ToArray();
        Require(parts.Length == expectedTexts.Count, "Prompt part count drifted.");
        for (var index = 0; index < parts.Length; index++)
        {
            Require(parts[index].GetProperty("type").GetString() == "text", "Prompt part type drifted.");
            Require(parts[index].GetProperty("text").GetString() == expectedTexts[index],
                "Prompt text drifted.");
            Require(parts[index].EnumerateObject().Select(property => property.Name)
                    .SequenceEqual(["type", "text"]),
                "Prompt part included an unplanned property.");
        }
    }

    private void Record(
        ContractualOpenCodeSessionServer server,
        int expectedRequests,
        int expectedMutations)
    {
        var requests = server.Requests;
        Require(requests.Count == expectedRequests,
            $"Request count drifted: expected {expectedRequests}, actual {requests.Count}.");
        foreach (var request in requests)
        {
            Require(IsAllowedRequest(request),
                $"{NoUnplannedMutationMarker}: {request.Method} {request.Path}");
        }
        var mutations = requests.Count(request => request.Method == "POST");
        Require(mutations == expectedMutations,
            $"Mutation count drifted: expected {expectedMutations}, actual {mutations}.");
        _requestCount += requests.Count;
        _mutationCount += mutations;
        _scenarioCount++;
    }

    private static bool IsAllowedRequest(ContractualSessionRequest request)
    {
        if (request.Method == "GET" && request.Path is "/global/health" or "/doc" or "/session/status")
        {
            return true;
        }
        if (request.Method == "POST" && request.Path == "/session")
        {
            return true;
        }
        if (!request.Path.StartsWith("/session/", StringComparison.Ordinal))
        {
            return false;
        }
        if (request.Method == "GET")
        {
            return request.Path.Count(character => character == '/') == 2;
        }
        if (request.Method == "POST")
        {
            return request.Path.EndsWith("/prompt_async", StringComparison.Ordinal) ||
                   request.Path.EndsWith("/abort", StringComparison.Ordinal);
        }
        return false;
    }

    private static int Count(
        ContractualOpenCodeSessionServer server,
        string method,
        string path) =>
        server.Requests.Count(request => request.Method == method && request.Path == path);

    private static async Task ExpectCodeAsync(Func<Task> action, string expectedCode)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OpenCodeSessionLifecycleException exception)
        {
            Require(exception.Code == expectedCode,
                $"Expected error {expectedCode}, actual {exception.Code}.");
            Require(!exception.Message.Contains("http", StringComparison.OrdinalIgnoreCase),
                "Lifecycle exception leaked endpoint context.");
            return;
        }
        throw new InvalidOperationException($"Expected OpenCode error {expectedCode}.");
    }

    private static async Task ExpectArgumentAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidOperationException("Expected bounded input validation failure.");
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
        throw new InvalidOperationException("Expected caller cancellation.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed record OpenCodeSessionLifecycleJourneyReport(
    int Scenarios,
    int Requests,
    int Mutations,
    string MutationGate,
    string ResultMarker);
