using BookStudio.Application.Diagnostics;
using BookStudio.Application.Persistence;
using BookStudio.Infrastructure.Diagnostics;
using BookStudio.Infrastructure.Persistence.Sqlite;
using Microsoft.AspNetCore.Http.Features;

namespace BookStudio.ControlCenter;

/// <summary>Composition root for the versioned local Control Center API.</summary>
public static class ControlCenterApplication
{
    private const string CorrelationHeader = "X-Correlation-ID";
    private const string ServiceName = "BookStudio.ControlCenter";

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var options = ControlCenterHostOptions.FromConfiguration(builder.Configuration);
        builder.WebHost.UseUrls(options.Url);

        builder.Services.AddProblemDetails(problemOptions =>
        {
            problemOptions.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
                context.ProblemDetails.Extensions["service"] = ServiceName;
            };
        });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(
            SqliteWorkspaceOptions.Create(options.WorkspaceRoot));
        builder.Services.AddSingleton<SqliteWorkspaceDatabase>();
        builder.Services.AddSingleton<IWorkspaceDatabaseLifecycle>(
            services => services.GetRequiredService<SqliteWorkspaceDatabase>());
        builder.Services.AddSingleton<IReadinessProbe, WorkspaceDatabaseReadinessProbe>();
        builder.Services.AddHostedService<WorkspaceDatabaseInitializationService>();

        var app = builder.Build();
        var startedAtUtc = DateTimeOffset.UtcNow;

        app.Use(async (context, next) =>
        {
            var incoming = context.Request.Headers[CorrelationHeader].FirstOrDefault();
            var correlationId = IsSafeCorrelationId(incoming)
                ? incoming!
                : Guid.NewGuid().ToString("N");
            context.TraceIdentifier = correlationId;
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[CorrelationHeader] = correlationId;
                return Task.CompletedTask;
            });
            await next(context).ConfigureAwait(false);
        });
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        static object LivePayload() => new
        {
            status = "live",
            service = ServiceName,
            version = GetVersion(),
        };

        app.MapGet("/health/live", () => Results.Ok(LivePayload()));
        app.MapGet("/health", () => Results.Ok(LivePayload()));

        app.MapGet(
            "/health/ready",
            async (IEnumerable<IReadinessProbe> probes, CancellationToken cancellationToken) =>
            {
                var checks = await CheckAllAsync(probes, cancellationToken).ConfigureAwait(false);
                var ready = checks.All(check => check.Ready);
                return Results.Json(
                    new
                    {
                        status = ready ? "ready" : "notReady",
                        service = ServiceName,
                        checks,
                    },
                    statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
            });

        app.MapGet(
            "/api/v1/diagnostics",
            async (
                IEnumerable<IReadinessProbe> probes,
                IWebHostEnvironment environment,
                CancellationToken cancellationToken) =>
            {
                var checks = await CheckAllAsync(probes, cancellationToken).ConfigureAwait(false);
                var ready = checks.All(check => check.Ready);
                var uptime = DateTimeOffset.UtcNow - startedAtUtc;
                return Results.Ok(new
                {
                    service = ServiceName,
                    version = GetVersion(),
                    environment = environment.EnvironmentName,
                    status = ready ? "ready" : "notReady",
                    uptimeSeconds = Math.Max(0, (long)uptime.TotalSeconds),
                    checks,
                });
            });

        return app;
    }

    private static async Task<IReadOnlyList<ReadinessProbeResult>> CheckAllAsync(
        IEnumerable<IReadinessProbe> probes,
        CancellationToken cancellationToken)
    {
        var results = new List<ReadinessProbeResult>();
        foreach (var probe in probes.OrderBy(probe => probe.Name, StringComparer.Ordinal))
        {
            results.Add(await probe.CheckAsync(cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    private static bool IsSafeCorrelationId(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Length <= 128 &&
               value.All(character => !char.IsControl(character));
    }

    private static string GetVersion() =>
        typeof(ControlCenterApplication).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
