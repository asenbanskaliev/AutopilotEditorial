using System.Net;
using System.Text.Json;
using BookStudio.ControlCenter;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

var root = Path.Combine(
    Path.GetTempPath(),
    "BookStudio.Tests.Api",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    VerifyRemoteBindingRejected(root);
    await VerifyHealthyHostAsync(Path.Combine(root, "healthy"));
    await VerifyUnreadyHostAsync(root);
    Console.WriteLine("API health integration PASS: binding, live, ready, diagnostics, correlation, problems and shutdown verified.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("API health integration FAIL: " + exception);
    return 1;
}
finally
{
    TryDelete(root);
}

static void VerifyRemoteBindingRejected(string workspaceRoot)
{
    RequireThrows<InvalidOperationException>(() =>
    {
        _ = ControlCenterApplication.Build(
        [
            "--ControlCenter:Url=http://0.0.0.0:5074",
            $"--ControlCenter:WorkspaceRoot={workspaceRoot}",
        ]);
    });
}

static async Task VerifyHealthyHostAsync(string workspaceRoot)
{
    Directory.CreateDirectory(workspaceRoot);
    var app = ControlCenterApplication.Build(
    [
        "--ControlCenter:Url=http://127.0.0.1:0",
        $"--ControlCenter:WorkspaceRoot={workspaceRoot}",
        "--environment=Integration",
    ]);

    try
    {
        await app.StartAsync();
        using var client = CreateClient(app);

        using (var live = await client.GetAsync("/health/live"))
        {
            Require(live.StatusCode == HttpStatusCode.OK, "Liveness must return 200.");
            Require(live.Headers.Contains("X-Correlation-ID"), "Liveness must return a correlation ID.");
            var body = await live.Content.ReadAsStringAsync();
            Require(JsonStatus(body) == "live", "Liveness payload is invalid.");
        }

        using (var legacy = await client.GetAsync("/health"))
        {
            Require(legacy.StatusCode == HttpStatusCode.OK, "Legacy health alias must return 200.");
            Require(JsonStatus(await legacy.Content.ReadAsStringAsync()) == "live", "Legacy health payload is invalid.");
        }

        using (var ready = await client.GetAsync("/health/ready"))
        {
            Require(ready.StatusCode == HttpStatusCode.OK, "Healthy readiness must return 200.");
            Require(JsonStatus(await ready.Content.ReadAsStringAsync()) == "ready", "Readiness payload is invalid.");
        }

        using (var diagnostics = await client.GetAsync("/api/v1/diagnostics"))
        {
            Require(diagnostics.StatusCode == HttpStatusCode.OK, "Diagnostics must return 200.");
            var body = await diagnostics.Content.ReadAsStringAsync();
            Require(JsonStatus(body) == "ready", "Diagnostics readiness is invalid.");
            AssertSanitized(body, workspaceRoot);
        }

        using (var problemRequest = new HttpRequestMessage(HttpMethod.Get, "/missing-route"))
        {
            problemRequest.Headers.Add("X-Correlation-ID", "api-test-correlation");
            using var problem = await client.SendAsync(problemRequest);
            Require(problem.StatusCode == HttpStatusCode.NotFound, "Unknown route must return 404.");
            Require(
                problem.Content.Headers.ContentType?.MediaType == "application/problem+json",
                "Unknown route must return Problem Details.");
            Require(
                problem.Headers.TryGetValues("X-Correlation-ID", out var values) &&
                values.Single() == "api-test-correlation",
                "A safe incoming correlation ID must be preserved.");
            var body = await problem.Content.ReadAsStringAsync();
            Require(body.Contains("api-test-correlation", StringComparison.Ordinal), "Problem Details must include correlation ID.");
            AssertSanitized(body, workspaceRoot);
        }

        using (var invalidCorrelationRequest = new HttpRequestMessage(HttpMethod.Get, "/health/live"))
        {
            var invalid = new string('x', 129);
            invalidCorrelationRequest.Headers.Add("X-Correlation-ID", invalid);
            using var response = await client.SendAsync(invalidCorrelationRequest);
            var returned = response.Headers.GetValues("X-Correlation-ID").Single();
            Require(returned != invalid && returned.Length > 0, "Unsafe correlation ID must be replaced.");
        }
    }
    finally
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

static async Task VerifyUnreadyHostAsync(string parentRoot)
{
    var blockedRoot = Path.Combine(parentRoot, "blocked-workspace");
    await File.WriteAllTextAsync(blockedRoot, "this path is a file");
    var app = ControlCenterApplication.Build(
    [
        "--ControlCenter:Url=http://127.0.0.1:0",
        $"--ControlCenter:WorkspaceRoot={blockedRoot}",
        "--environment=Integration",
    ]);

    try
    {
        await app.StartAsync();
        using var client = CreateClient(app);
        using var live = await client.GetAsync("/health/live");
        Require(live.StatusCode == HttpStatusCode.OK, "Dependency failure must not break liveness.");

        using var ready = await client.GetAsync("/health/ready");
        Require(ready.StatusCode == HttpStatusCode.ServiceUnavailable, "Unhealthy readiness must return 503.");
        Require(JsonStatus(await ready.Content.ReadAsStringAsync()) == "notReady", "Unready payload is invalid.");

        using var diagnostics = await client.GetAsync("/api/v1/diagnostics");
        var diagnosticsBody = await diagnostics.Content.ReadAsStringAsync();
        Require(JsonStatus(diagnosticsBody) == "notReady", "Diagnostics must expose sanitized not-ready status.");
        AssertSanitized(diagnosticsBody, blockedRoot);
    }
    finally
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

static HttpClient CreateClient(WebApplication app)
{
    var server = app.Services.GetRequiredService<IServer>();
    var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
        ?? throw new InvalidOperationException("Kestrel did not expose bound addresses.");
    var address = addresses.Single(value => value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
    return new HttpClient { BaseAddress = new Uri(address) };
}

static string JsonStatus(string json)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.GetProperty("status").GetString()
        ?? throw new InvalidOperationException("Response status is missing.");
}

static void AssertSanitized(string body, string workspaceRoot)
{
    foreach (var forbidden in new[]
             {
                 workspaceRoot,
                 "connectionstring",
                 "password",
                 "secret",
                 "stacktrace",
                 "exception.message",
             })
    {
        Require(
            !body.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            $"Response leaked forbidden diagnostic content: {forbidden}");
    }
}

static void RequireThrows<TException>(Action operation)
    where TException : Exception
{
    try
    {
        operation();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected exception {typeof(TException).Name} was not thrown.");
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
        // Integration cleanup is best effort after the hosts have stopped.
    }
    catch (UnauthorizedAccessException)
    {
        // Integration cleanup is best effort after the hosts have stopped.
    }
}
