using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PythonScriptRunner;
using PythonScriptRunner.RabbitMQ;
using PythonScriptRunner.Worker;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the PythonScriptRunner worker.</summary>
public static class WorkerServiceCollectionExtensions
{
    /// <summary>
    /// Runs queued steps in this process (requires <c>AddEntityFrameworkCore</c> and <c>AddRabbitMq</c>): one message per step,
    /// leases with recovery, automatic retries, per-worker and per-pipeline concurrency, cancellation, the cron scheduler and the
    /// cleanup of work folders. The host shutdown timeout is raised to cover <see cref="WorkerOptions.ShutdownGracePeriod"/>.
    /// </summary>
    /// <param name="builder">Builder returned by <c>AddPythonScriptRunner</c>.</param>
    /// <param name="configure">Worker settings.</param>
    public static IPythonScriptRunnerBuilder AddWorker(this IPythonScriptRunnerBuilder builder, Action<WorkerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        if (!services.Any(d => d.ServiceType == typeof(PsrBroker)))
        {
            throw new PsrConfigurationException("AddWorker requires AddEntityFrameworkCore(...) and AddRabbitMq(...) first.");
        }

        var options = new WorkerOptions();
        configure?.Invoke(options);
        options.Validate();

        services.AddSingleton(options);
        services.TryAddSingleton<WorkerMetrics>();
        services.TryAddSingleton<StepWorker>();
        services.Configure<HostOptions>(host =>
        {
            var needed = options.ShutdownGracePeriod + TimeSpan.FromSeconds(30);
            if (host.ShutdownTimeout < needed)
            {
                host.ShutdownTimeout = needed;
            }
        });
        services.AddHostedService<WorkFolderJanitor>();
        services.AddHostedService<LeaseReaper>();
        if (options.Scheduler.Enabled)
        {
            services.AddHostedService<ScheduleFirer>();
        }

        // Registered last so it stops first: steps in progress finish while the relay still publishes their messages.
        services.AddHostedService(provider => provider.GetRequiredService<StepWorker>());
        return builder;
    }

    /// <summary>
    /// Health of a PythonScriptRunner worker (spec 006, AC-16): the database (unhealthy when the schema is pending or no
    /// partitions are created ahead), the broker (degraded while unreachable: runs wait in the outbox) and uv (unhealthy when
    /// missing). All checks are tagged <c>ready</c>.
    /// </summary>
    /// <param name="builder">Health checks builder.</param>
    public static IHealthChecksBuilder AddPythonScriptRunnerChecks(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        string[] ready = ["ready"];
        builder.AddPythonScriptRunnerDatabase(tags: ready);
        builder.AddPythonScriptRunnerBroker(tags: ready);
        builder.Services.TryAddSingleton<UvHealthCheck>();
        return builder.Add(new HealthCheckRegistration("psr-uv", provider => provider.GetRequiredService<UvHealthCheck>(), HealthStatus.Unhealthy, ready));
    }
}

/// <summary>uv must be available to create the Python environments.</summary>
internal sealed class UvHealthCheck(IOptions<PythonScriptRunnerOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var uv = Find(options.Value.UvPath);
        return Task.FromResult(uv is not null
            ? HealthCheckResult.Healthy($"uv found at {uv}.")
            : new HealthCheckResult(context.Registration.FailureStatus, $"uv was not found ('{options.Value.UvPath}'); Python environments cannot be created."));
    }

    private static string? Find(string nameOrPath)
    {
        string[] names = OperatingSystem.IsWindows() && !Path.HasExtension(nameOrPath) ? [nameOrPath, nameOrPath + ".exe"] : [nameOrPath];
        if (Path.IsPathRooted(nameOrPath) || nameOrPath.Contains('/', StringComparison.Ordinal) || nameOrPath.Contains('\\', StringComparison.Ordinal))
        {
            return names.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(directory => names.Select(name => Path.Combine(directory, name)))
            .FirstOrDefault(File.Exists);
    }
}
