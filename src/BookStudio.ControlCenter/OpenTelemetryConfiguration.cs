using BookStudio.Application.Observability;
using BookStudio.Infrastructure.Observability;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BookStudio.ControlCenter;

/// <summary>Configures the BookStudio OpenTelemetry SDK and bounded local exporters.</summary>
public static class OpenTelemetryConfiguration
{
    private const string ServiceName = "BookStudio.ControlCenter";
    private static readonly HashSet<string> TraceablePaths = new(StringComparer.Ordinal)
    {
        "/",
        "/system",
        "/configuration",
        "/about",
        "/app.css",
        "/app.js",
        "/health",
        "/health/live",
        "/health/ready",
        "/api/v1/diagnostics",
        "/api/v1/configuration",
    };

    public static TelemetrySnapshotStore AddBookStudioOpenTelemetry(
        this WebApplicationBuilder builder,
        ObservabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var store = new TelemetrySnapshotStore(
            options.Enabled,
            options.OtlpEnabled,
            options.SnapshotCapacityPerSignal);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IObservabilitySnapshotReader>(store);

        if (!options.Enabled)
        {
            return store;
        }

        var serviceVersion = typeof(OpenTelemetryConfiguration).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";
        var instanceId = Guid.NewGuid().ToString("N");

        var openTelemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    ServiceName,
                    serviceVersion: serviceVersion,
                    serviceInstanceId: instanceId)
                .AddAttributes(
                [
                    new KeyValuePair<string, object>(
                        "deployment.environment.name",
                        builder.Environment.EnvironmentName),
                ]))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(BookStudioTelemetry.InstrumentationName)
                    .SetSampler(new ParentBasedSampler(
                        new TraceIdRatioBasedSampler(options.TraceSamplingRatio)))
                    .AddAspNetCoreInstrumentation(instrumentation =>
                    {
                        instrumentation.Filter = context =>
                            context.Request.Path.Value is { } path && TraceablePaths.Contains(path);
                        instrumentation.RecordException = false;
                    })
                    .AddProcessor(new SimpleActivityExportProcessor(
                        new SnapshotActivityExporter(store)));

                if (options.OtlpEnabled)
                {
                    tracing.AddOtlpExporter(exporter =>
                    {
                        exporter.Endpoint = options.OtlpEndpoint!;
                        exporter.Protocol = OtlpExportProtocol.Grpc;
                    });
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(BookStudioTelemetry.InstrumentationName)
                    .AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddReader(new PeriodicExportingMetricReader(
                        new SnapshotMetricExporter(store),
                        exportIntervalMilliseconds: 60_000,
                        exportTimeoutMilliseconds: 30_000));

                if (options.OtlpEnabled)
                {
                    metrics.AddOtlpExporter(exporter =>
                    {
                        exporter.Endpoint = options.OtlpEndpoint!;
                        exporter.Protocol = OtlpExportProtocol.Grpc;
                    });
                }
            });

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeScopes = true;
            logging.IncludeFormattedMessage = false;
            logging.ParseStateValues = true;
            logging.AddProcessor(new SimpleLogRecordExportProcessor(
                new SnapshotLogExporter(store)));

            if (options.OtlpEnabled)
            {
                logging.AddOtlpExporter(exporter =>
                {
                    exporter.Endpoint = options.OtlpEndpoint!;
                    exporter.Protocol = OtlpExportProtocol.Grpc;
                });
            }
        });

        _ = openTelemetry;
        return store;
    }
}
