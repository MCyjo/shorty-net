using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Shorty.ServiceDefaults;

/// <summary>Cross-cutting setup shared by the web services: telemetry, graceful shutdown, health endpoints.</summary>
public static class ServiceDefaultsExtensions
{
    public const string ReadyTag = "ready";

    /// <summary>
    /// Exporters are opt-in through configuration, so the same build runs locally and in Azure:
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> sends OTLP (Aspire Dashboard, any collector) and
    /// <c>Telemetry:Console=true</c> prints spans to stdout.
    /// </summary>
    public static WebApplicationBuilder AddShortyServiceDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Let in-flight requests finish before the container is stopped.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeScopes = true;
            o.IncludeFormattedMessage = true;
        });

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o =>
                    // Probes and scrapes would otherwise dominate the traces of a service that is meant to serve redirects.
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
                .AddHttpClientInstrumentation()
                .AddSource("Npgsql"))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            otel.UseOtlpExporter();

        if (builder.Configuration.GetValue<bool>("Telemetry:Console"))
            otel.WithTracing(t => t.AddConsoleExporter());

        return builder;
    }

    /// <summary>
    /// <c>/health/live</c>: the process is up (liveness; no dependencies, so a database or cache outage never restarts the container).
    /// <c>/health/ready</c>: dependencies tagged "ready" are healthy (readiness).
    /// </summary>
    public static WebApplication MapShortyHealthChecks(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains(ReadyTag) });
        return app;
    }
}
