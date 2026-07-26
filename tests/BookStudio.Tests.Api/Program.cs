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
    Console.WriteLine("API and shell integration PASS: binding, health, diagnostics, shell, deep links, security and shutdown verified.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("API and shell integration FAIL: " + exception);
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
            AssertSecurityHeaders(live);
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

        using (var configuration = await client.GetAsync("/api/v1/configuration"))
        {
            Require(configuration.StatusCode == HttpStatusCode.OK, "Safe configuration must return 200.");
            var body = await configuration.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var rootElement = document.RootElement;
            Require(rootElement.GetProperty("apiVersion").GetString() == "v1", "API version is invalid.");
            Require(rootElement.GetProperty("bindScope").GetString() == "loopback", "Default bind scope must be loopback.");
            Require(!rootElement.GetProperty("remoteBindingEnabled").GetBoolean(), "Remote binding must be disabled by default.");
            Require(
                rootElement.GetProperty("supportedThemes").EnumerateArray().Select(item => item.GetString()).SequenceEqual(["system", "light", "dark"]),
                "Supported theme contract is invalid.");
            Require(
                rootElement.GetProperty("supportedRefreshIntervalsSeconds").EnumerateArray().Select(item => item.GetInt32()).SequenceEqual([0, 5, 15, 30, 60]),
                "Supported refresh intervals are invalid.");
            AssertSanitized(body, workspaceRoot);
            Require(!body.Contains("url", StringComparison.OrdinalIgnoreCase), "Configuration must not expose the listen URL.");
        }

        foreach (var route in new[] { "/", "/system", "/configuration", "/about" })
        {
            await AssertShellAsync(client, route);
        }

        using (var css = await client.GetAsync("/app.css"))
        {
            Require(css.StatusCode == HttpStatusCode.OK, "Shell CSS must return 200.");
            Require(css.Content.Headers.ContentType?.MediaType == "text/css", "Shell CSS content type is invalid.");
            Require(css.Headers.CacheControl?.MaxAge == TimeSpan.FromHours(1), "Static assets must use the configured cache duration.");
            var body = await css.Content.ReadAsStringAsync();
            Require(body.Contains(":focus-visible", StringComparison.Ordinal), "Shell CSS lacks visible focus support.");
            Require(body.Contains("prefers-reduced-motion", StringComparison.Ordinal), "Shell CSS lacks reduced-motion support.");
            AssertSecurityHeaders(css);
        }

        using (var script = await client.GetAsync("/app.js"))
        {
            Require(script.StatusCode == HttpStatusCode.OK, "Shell JavaScript must return 200.");
            Require(
                script.Content.Headers.ContentType?.MediaType is "text/javascript" or "application/javascript",
                "Shell JavaScript content type is invalid.");
            var body = await script.Content.ReadAsStringAsync();
            Require(body.Contains("/api/v1/diagnostics", StringComparison.Ordinal), "Shell does not consume diagnostics API.");
            Require(body.Contains("localStorage", StringComparison.Ordinal), "Shell does not persist local preferences.");
            AssertSecurityHeaders(script);
        }

        await AssertProblemAsync(client, "/api/v1/unknown", workspaceRoot);
        await AssertProblemAsync(client, "/health/unknown", workspaceRoot);
        await AssertProblemAsync(client, "/unknown-page", workspaceRoot);

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

        using var shell = await client.GetAsync("/");
        Require(shell.StatusCode == HttpStatusCode.OK, "Shell must remain available while a dependency is unready.");
        Require(shell.Content.Headers.ContentType?.MediaType == "text/html", "Unready shell must remain HTML.");
    }
    finally
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

static async Task AssertShellAsync(HttpClient client, string route)
{
    using var response = await client.GetAsync(route);
    Require(response.StatusCode == HttpStatusCode.OK, $"Shell route {route} must return 200.");
    Require(response.Content.Headers.ContentType?.MediaType == "text/html", $"Shell route {route} must return HTML.");
    Require(response.Headers.CacheControl?.NoStore == true, $"Shell route {route} must use no-store.");
    AssertSecurityHeaders(response);
    var body = await response.Content.ReadAsStringAsync();
    Require(body.Contains("Autopilot Editorial", StringComparison.Ordinal), "Shell brand marker is missing.");
    Require(body.Contains("data-route=\"/configuration\"", StringComparison.Ordinal), "Shell navigation marker is missing.");
    Require(body.Contains("aria-live", StringComparison.Ordinal), "Shell live-region marker is missing.");
    Require(!body.Contains("http://", StringComparison.OrdinalIgnoreCase), "Shell contains an external or absolute HTTP asset.");
    Require(!body.Contains("https://", StringComparison.OrdinalIgnoreCase), "Shell contains an external HTTPS asset.");
}

static async Task AssertProblemAsync(HttpClient client, string route, string workspaceRoot)
{
    using var response = await client.GetAsync(route);
    Require(response.StatusCode == HttpStatusCode.NotFound, $"Unknown route {route} must return 404.");
    Require(
        response.Content.Headers.ContentType?.MediaType == "application/problem+json",
        $"Unknown route {route} must remain Problem Details, not shell HTML.");
    var body = await response.Content.ReadAsStringAsync();
    Require(!body.Contains("<!doctype html>", StringComparison.OrdinalIgnoreCase), "Problem response was replaced by the shell.");
    AssertSanitized(body, workspaceRoot);
}

static void AssertSecurityHeaders(HttpResponseMessage response)
{
    var csp = response.Headers.GetValues("Content-Security-Policy").Single();
    Require(csp.Contains("default-src 'self'", StringComparison.Ordinal), "CSP default-src is missing.");
    Require(csp.Contains("object-src 'none'", StringComparison.Ordinal), "CSP object-src is missing.");
    Require(csp.Contains("frame-ancestors 'none'", StringComparison.Ordinal), "CSP frame-ancestors is missing.");
    Require(response.Headers.GetValues("X-Content-Type-Options").Single() == "nosniff", "nosniff header is missing.");
    Require(response.Headers.GetValues("Referrer-Policy").Single() == "no-referrer", "Referrer policy is invalid.");
    Require(response.Headers.GetValues("X-Frame-Options").Single() == "DENY", "Frame policy is invalid.");
    Require(response.Headers.Contains("Permissions-Policy"), "Permissions policy is missing.");
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
