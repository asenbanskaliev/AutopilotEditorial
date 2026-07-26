using BookStudio.Application.Diagnostics;
using BookStudio.Application.Persistence;
using BookStudio.Infrastructure.Diagnostics;
using BookStudio.Infrastructure.Persistence.Sqlite;
using Microsoft.AspNetCore.StaticFiles;

namespace BookStudio.ControlCenter;

/// <summary>Composition root for the versioned local Control Center API and shell.</summary>
public static class ControlCenterApplication
{
    private const string CorrelationHeader = "X-Correlation-ID";
    private const string ServiceName = "BookStudio.ControlCenter";
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self'; " +
        "img-src 'self' data:; " +
        "connect-src 'self'; " +
        "font-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "frame-ancestors 'none'; " +
        "form-action 'self'";

    private static readonly string[] ShellRoutes = ["/", "/system", "/configuration", "/about"];
    private static readonly string[] SupportedThemes = ["system", "light", "dark"];
    private static readonly int[] SupportedRefreshIntervalsSeconds = [0, 5, 15, 30, 60];

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var options = ControlCenterHostOptions.FromConfiguration(builder.Configuration);
        var webRootPath = ResolveWebRootPath(builder.Environment.ContentRootPath);
        builder.WebHost.UseUrls(options.Url);
        builder.WebHost.UseWebRoot(webRootPath);

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
        var indexPath = Path.Combine(webRootPath, "index.html");

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
                context.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers.ReferrerPolicy = "no-referrer";
                context.Response.Headers.XFrameOptions = "DENY";
                context.Response.Headers["Permissions-Policy"] =
                    "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
                return Task.CompletedTask;
            });
            await next(context).ConfigureAwait(false);
        });
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                context.Context.Response.Headers.CacheControl =
                    context.Context.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)
                        ? "no-store"
                        : "public,max-age=3600";
            },
        });

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

        app.MapGet(
            "/api/v1/configuration",
            () => Results.Ok(new
            {
                apiVersion = "v1",
                bindScope = options.AllowRemoteBinding ? "remote-enabled" : "loopback",
                remoteBindingEnabled = options.AllowRemoteBinding,
                supportedThemes = SupportedThemes,
                supportedRefreshIntervalsSeconds = SupportedRefreshIntervalsSeconds,
            }));

        foreach (var route in ShellRoutes)
        {
            app.MapGet(
                route,
                (HttpContext context) =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    return Results.File(indexPath, "text/html; charset=utf-8");
                });
        }

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

    private static string ResolveWebRootPath(string contentRootPath)
    {
        var candidates = new List<string>
        {
            Path.Combine(contentRootPath, "wwwroot"),
            Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        };

        foreach (var startingPath in new[] { contentRootPath, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(startingPath);
                 directory is not null;
                 directory = directory.Parent)
            {
                candidates.Add(Path.Combine(
                    directory.FullName,
                    "src",
                    "BookStudio.ControlCenter",
                    "wwwroot"));
            }
        }

        var resolved = candidates
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, "index.html")));
        return resolved ?? throw new InvalidOperationException(
            "Control Center shell assets were not found in the application or repository layout.");
    }

    private static IEqualityComparer<string> PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string GetVersion() =>
        typeof(ControlCenterApplication).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
