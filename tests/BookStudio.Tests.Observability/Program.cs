using System.Diagnostics;
using System.Net;
using System.Text.Json;
using BookStudio.Application.Observability;
using BookStudio.ControlCenter;
using BookStudio.Infrastructure.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var root = Path.Combine(
    Path.GetTempPath(),
    "BookStudio.Tests.Observability",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    VerifyOptionsValidation();
    VerifyBoundedStore();
    await VerifyDisabledModeAsync(Path.Combine(root, "disabled"));
    await VerifyOpenTelemetryPipelineAsync(Path.Combine(root, "enabled"));
    Console.WriteLine(
        "OpenTelemetry integration PASS: traces, metrics, logs, propagation, redaction, bounds, force flush and safe API verified.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("OpenTelemetry integration FAIL: " + exception);
    return 1;
}
finally
{
    TryDelete(root);
}

static void VerifyOptionsValidation()
{
    RequireThrows<InvalidOperationException>(() =>
        new ObservabilityOptions(true, 15, 1, false, null).Validate());
    RequireThrows<InvalidOperationException>(() =>
        new ObservabilityOptions(true, 16, -0.01, false, null).Validate());
    RequireThrows<InvalidOperationException>(() =>
        new ObservabilityOptions(true, 16, 1, true, null).Validate());
    RequireThrows<InvalidOperationException>(() =>
        new ObservabilityOptions(
            true,
            16,
            1,
            true,
            new Uri("http://collector.example.com:4317")).Validate());
    RequireThrows<InvalidOperationException>(() =>
        new ObservabilityOptions(
            true,
            16,
            1,
            true,
            new Uri("https://user:password@collector.example.com:4317")).Validate());
    RequireThrows<InvalidOperationException>(() =>
        new ObservabilityOptions(
            true,
            16,
            1,
            true,
            new Uri("https://collector.example.com:4317?token=secret")).Validate());

    new ObservabilityOptions(
        true,
        16,
        1,
        true,
        new Uri("https://collector.example.com:4317")).Validate();
    new ObservabilityOptions(
        true,
        16,
        1,
        true,
        new Uri("http://127.0.0.1:4317")).Validate();
}

static void VerifyBoundedStore()
{
    var store = new TelemetrySnapshotStore(enabled: true, otlpEnabled: false, capacityPerSignal: 16);
    var emptyAttributes = new Dictionary<string, string>(StringComparer.Ordinal);

    for (var index = 0; index < 20; index++)
    {
        store.RecordTrace(new TraceSnapshotRecord(
            DateTimeOffset.UnixEpoch.AddSeconds(index),
            index.ToString("x32"),
            index.ToString("x16"),
            $"trace-{index}",
            "Internal",
            "Ok",
            index,
            emptyAttributes));
        store.RecordMetric(new MetricSnapshotRecord(
            DateTimeOffset.UnixEpoch.AddSeconds(index),
            $"metric-{index}",
            "LongSum",
            "{item}"));
        store.RecordLog(new LogSnapshotRecord(
            DateTimeOffset.UnixEpoch.AddSeconds(index),
            "Information",
            "BookStudio.Tests.Observability",
            $"log-{index}",
            null,
            null,
            emptyAttributes));
    }

    var snapshot = store.Read(100);
    Require(snapshot.TraceCount == 16 && snapshot.DroppedTraceCount == 4, "Trace buffer bounds are invalid.");
    Require(snapshot.MetricCount == 16 && snapshot.DroppedMetricCount == 4, "Metric buffer bounds are invalid.");
    Require(snapshot.LogCount == 16 && snapshot.DroppedLogCount == 4, "Log buffer bounds are invalid.");
    Require(snapshot.Traces[0].Name == "trace-19", "Trace records must be newest first.");
    Require(snapshot.Metrics[0].Name == "metric-19", "Metric records must be newest first.");
    Require(snapshot.Logs[0].MessageTemplate == "log-19", "Log records must be newest first.");

    var sanitized = TelemetrySnapshotStore.SanitizeAttributes(
    [
        new KeyValuePair<string, object?>("bookstudio.safe_code", "OBS-SAFE"),
        new KeyValuePair<string, object?>("workspace.path", "/private/workspace"),
        new KeyValuePair<string, object?>("secret_token", "do-not-export"),
        new KeyValuePair<string, object?>("unknown.high_cardinality", "do-not-export"),
    ]);
    Require(sanitized.Count == 1 && sanitized["bookstudio.safe_code"] == "OBS-SAFE", "Attribute redaction is invalid.");
}

static async Task VerifyDisabledModeAsync(string workspaceRoot)
{
    Directory.CreateDirectory(workspaceRoot);
    var app = ControlCenterApplication.Build(
    [
        "--ControlCenter:Url=http://127.0.0.1:0",
        $"--ControlCenter:WorkspaceRoot={workspaceRoot}",
        "--Observability:Enabled=false",
        "--environment=Integration",
    ]);

    try
    {
        var snapshot = app.Services.GetRequiredService<IObservabilitySnapshotReader>().Read(10);
        Require(!snapshot.Enabled, "Disabled observability must report disabled.");
        Require(snapshot.TraceCount == 0 && snapshot.MetricCount == 0 && snapshot.LogCount == 0, "Disabled observability must remain empty.");
    }
    finally
    {
        await app.DisposeAsync();
    }
}

static async Task VerifyOpenTelemetryPipelineAsync(string workspaceRoot)
{
    Directory.CreateDirectory(workspaceRoot);
    const string secretToken = "otel-secret-token-should-never-appear";
    var secretPath = Path.Combine(workspaceRoot, "private", "manuscript.md");
    const string exceptionSecret = "otel-exception-message-should-never-appear";
    const string propagatedTraceId = "11111111111111111111111111111111";
    const string propagatedParentSpanId = "2222222222222222";

    var app = ControlCenterApplication.Build(
    [
        "--ControlCenter:Url=http://127.0.0.1:0",
        $"--ControlCenter:WorkspaceRoot={workspaceRoot}",
        "--Observability:Enabled=true",
        "--Observability:SnapshotCapacityPerSignal=16",
        "--Observability:TraceSamplingRatio=1",
        "--Observability:OtlpEnabled=false",
        "--environment=Integration",
    ]);

    try
    {
        await app.StartAsync();
        using var client = CreateClient(app);
        var logger = app.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("BookStudio.Tests.Observability");

        for (var index = 0; index < 24; index++)
        {
            var operationName = index % 5 == 0
                ? "integration.telemetry.failure"
                : "integration.telemetry.success";
            var activity = BookStudioTelemetry.StartOperation(operationName)
                ?? throw new InvalidOperationException("The BookStudio ActivitySource was not sampled.");
            activity.SetTag("bookstudio.safe_code", $"OBS-{index:D2}");
            activity.SetTag("workspace.path", secretPath);
            activity.SetTag("secret_token", secretToken);

            using (logger.BeginScope(new Dictionary<string, object?>
                   {
                       ["bookstudio.safe_code"] = $"OBS-{index:D2}",
                       ["workspace.path"] = secretPath,
                       ["secret_token"] = secretToken,
                   }))
            {
                logger.LogInformation("OpenTelemetry integration iteration {Iteration}", index);
            }

            var succeeded = index % 5 != 0;
            BookStudioTelemetry.CompleteOperation(
                operationName,
                TimeSpan.FromMilliseconds(index + 1),
                succeeded,
                activity);
        }

        logger.LogError(
            new InvalidOperationException(exceptionSecret),
            "Controlled observability failure {Code}",
            "OBS-ERROR");

        using (var request = new HttpRequestMessage(HttpMethod.Get, "/health/live"))
        {
            request.Headers.TryAddWithoutValidation(
                "traceparent",
                $"00-{propagatedTraceId}-{propagatedParentSpanId}-01");
            using var response = await client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.OK, "Traced health request must succeed.");
        }

        var finalActivity = BookStudioTelemetry.StartOperation("integration.telemetry.final")
            ?? throw new InvalidOperationException("The final BookStudio activity was not sampled.");
        finalActivity.SetTag("bookstudio.safe_code", "OBS-FINAL");
        finalActivity.SetTag("workspace.path", secretPath);
        BookStudioTelemetry.CompleteOperation(
            "integration.telemetry.final",
            TimeSpan.FromMilliseconds(12),
            succeeded: true,
            finalActivity);
        logger.LogInformation("Final OpenTelemetry marker {Code}", "OBS-FINAL");

        var tracerProvider = app.Services.GetRequiredService<TracerProvider>();
        var meterProvider = app.Services.GetRequiredService<MeterProvider>();
        Require(tracerProvider.ForceFlush(10_000), "TracerProvider force flush failed.");
        Require(meterProvider.ForceFlush(10_000), "MeterProvider force flush failed.");

        var directSnapshot = app.Services
            .GetRequiredService<IObservabilitySnapshotReader>()
            .Read(100);
        Require(directSnapshot.Enabled && !directSnapshot.OtlpEnabled, "Observability mode is invalid.");
        Require(directSnapshot.CapacityPerSignal == 16, "Snapshot capacity mismatch.");
        Require(directSnapshot.TraceCount is > 0 and <= 16, "Trace snapshot count is invalid.");
        Require(directSnapshot.MetricCount is > 0 and <= 16, "Metric snapshot count is invalid.");
        Require(directSnapshot.LogCount is > 0 and <= 16, "Log snapshot count is invalid.");
        Require(directSnapshot.DroppedTraceCount > 0, "Trace overflow was not recorded.");
        Require(directSnapshot.DroppedLogCount > 0, "Log overflow was not recorded.");
        Require(
            directSnapshot.Traces.Any(trace => trace.TraceId == propagatedTraceId),
            "Incoming W3C trace context was not propagated.");
        Require(
            directSnapshot.Traces.Any(trace =>
                trace.Attributes.TryGetValue("bookstudio.safe_code", out var value) &&
                value == "OBS-FINAL"),
            "Allowlisted custom trace attributes were not exported.");
        Require(
            directSnapshot.Logs.Any(log => log.ExceptionType == typeof(InvalidOperationException).FullName),
            "Exception type was not retained safely.");

        using (var response = await client.GetAsync("/api/v1/observability?limit=100"))
        {
            Require(response.StatusCode == HttpStatusCode.OK, "Observability endpoint must return 200.");
            var body = await response.Content.ReadAsStringAsync();
            AssertSanitized(body, secretToken, secretPath, exceptionSecret);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Require(root.GetProperty("enabled").GetBoolean(), "Endpoint must report enabled observability.");
            Require(!root.GetProperty("otlpEnabled").GetBoolean(), "Endpoint must not report OTLP enabled.");
            Require(root.GetProperty("capacityPerSignal").GetInt32() == 16, "Endpoint capacity mismatch.");
            Require(root.GetProperty("traces").GetArrayLength() <= 16, "Endpoint trace limit was exceeded.");
            Require(root.GetProperty("metrics").GetArrayLength() <= 16, "Endpoint metric limit was exceeded.");
            Require(root.GetProperty("logs").GetArrayLength() <= 16, "Endpoint log limit was exceeded.");
        }

        using (var invalidLimit = await client.GetAsync("/api/v1/observability?limit=101"))
        {
            Require(invalidLimit.StatusCode == HttpStatusCode.BadRequest, "Invalid observability limit must return 400.");
            Require(
                invalidLimit.Content.Headers.ContentType?.MediaType == "application/problem+json",
                "Invalid observability limit must return Problem Details.");
        }

        using (var configuration = await client.GetAsync("/api/v1/configuration"))
        {
            var body = await configuration.Content.ReadAsStringAsync();
            Require(configuration.StatusCode == HttpStatusCode.OK, "Configuration endpoint must return 200.");
            Require(!body.Contains("OtlpEndpoint", StringComparison.OrdinalIgnoreCase), "Configuration leaked the OTLP endpoint field.");
            Require(!body.Contains("4317", StringComparison.Ordinal), "Configuration leaked an OTLP endpoint value.");
        }

        Require(tracerProvider.ForceFlush(10_000), "Final tracer flush failed.");
        var afterEndpoint = app.Services
            .GetRequiredService<IObservabilitySnapshotReader>()
            .Read(100);
        Require(
            afterEndpoint.Traces.All(trace =>
                !trace.Attributes.TryGetValue("http.route", out var route) ||
                route != "/api/v1/observability"),
            "The observability endpoint created recursive trace noise.");
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

static void AssertSanitized(string body, params string[] forbiddenValues)
{
    foreach (var forbidden in forbiddenValues)
    {
        Require(
            !body.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            $"Observability response leaked sensitive content: {forbidden}");
    }

    foreach (var forbiddenTerm in new[]
             {
                 "stackTrace",
                 "exceptionMessage",
                 "formattedMessage",
                 "requestBody",
                 "responseBody",
             })
    {
        Require(
            !body.Contains(forbiddenTerm, StringComparison.OrdinalIgnoreCase),
            $"Observability response exposed forbidden field: {forbiddenTerm}");
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
        // Best-effort cleanup after the host has stopped.
    }
    catch (UnauthorizedAccessException)
    {
        // Best-effort cleanup after the host has stopped.
    }
}
