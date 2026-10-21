using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Defaults of the standalone hosts (spec 009; D-54): Serilog writing JSON to the console, OpenTelemetry traces and metrics of
/// the <c>PythonScriptRunner</c> source/meter (exported with OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set) and the
/// <c>/health/live</c> and <c>/health/ready</c> endpoints.
/// </summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>Tag of the checks behind <c>/health/live</c>.</summary>
    public const string LiveTag = "live";

    /// <summary>Tag of the checks behind <c>/health/ready</c>.</summary>
    public const string ReadyTag = "ready";

    /// <summary>Adds logging, telemetry and the liveness check.</summary>
    /// <param name="builder">Host builder.</param>
    /// <param name="serviceName">Service name in logs and telemetry.</param>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        // ASP.NET Core logs every request at Information; the traces already cover them (D-187). The configuration
        // (Serilog:MinimumLevel:Override:Microsoft.AspNetCore) can bring them back.
        builder.Services.AddSerilog((services, logger) => logger
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service", serviceName)
            .WriteTo.Console(new RenderedCompactJsonFormatter()));

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing.AddSource("PythonScriptRunner").AddAspNetCoreInstrumentation())
            .WithMetrics(metrics => metrics.AddMeter("PythonScriptRunner").AddAspNetCoreInstrumentation().AddRuntimeInstrumentation());
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }

        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag]);
        return builder;
    }

    /// <summary>
    /// Maps <c>/health/live</c> (the process answers) and <c>/health/ready</c> (checks tagged <see cref="ReadyTag"/>: 200 when
    /// healthy or degraded, 503 when unhealthy).
    /// </summary>
    /// <param name="app">Application.</param>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = check => check.Tags.Contains(LiveTag) });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) });
        return app;
    }
}
