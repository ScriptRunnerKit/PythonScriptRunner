using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PythonScriptRunner;
using PythonScriptRunner.EntityFrameworkCore;
using PythonScriptRunner.RabbitMQ;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the RabbitMQ transport of PythonScriptRunner.</summary>
public static class RabbitMqServiceCollectionExtensions
{
    /// <summary>
    /// Adds queued runs over RabbitMQ (requires <c>AddEntityFrameworkCore</c>): <see cref="IRunQueue"/>, the outbox relay, the
    /// public <c>StartPipelineRun</c> command, run events (also for synchronous runs) and the cancellation of runs in any instance.
    /// Executing queued steps needs a worker (<c>AddWorker</c>).
    /// </summary>
    /// <param name="builder">Builder returned by <c>AddPythonScriptRunner</c>.</param>
    /// <param name="configure">Connection and behaviour.</param>
    public static IPythonScriptRunnerBuilder AddRabbitMq(this IPythonScriptRunnerBuilder builder, Action<RabbitMqOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var services = builder.Services;
        if (!services.Any(d => d.ServiceType == typeof(AsyncRunLifecycle)))
        {
            throw new PsrConfigurationException("AddRabbitMq requires the persistence package: call AddEntityFrameworkCore(...) first (queued runs live in the database).");
        }

        var options = new RabbitMqOptions();
        configure(options);
        options.Validate();

        services.AddSingleton(options);
        services.TryAddSingleton(provider => new PsrBroker(options, provider.GetRequiredService<ILogger<PsrBroker>>()));
        services.TryAddSingleton<RabbitMqMetrics>();
        services.Replace(ServiceDescriptor.Singleton<IRunMessageOutbox, RabbitMqRunMessageOutbox>());
        services.TryAddSingleton<IRunQueue, RabbitMqRunQueue>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RabbitMqStartup>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ControlService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RunEventSubscriptionHost>());
        if (options.RunOutboxRelay)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OutboxRelay>());
        }

        if (options.EnablePublicCommands)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, StartPipelineRunService>());
        }

        return builder;
    }

    /// <summary>
    /// Handles run events in <typeparamref name="THandler"/> (resolved in a scope per event), e.g.
    /// <c>.AddRunEventHandler&lt;NotifyErp&gt;(s =&gt; s.Subscription("erp").On("run.succeeded.calculo-progressao", "run.failed.*"))</c>.
    /// </summary>
    /// <typeparam name="THandler">Handler type.</typeparam>
    /// <param name="builder">Builder.</param>
    /// <param name="configure">Queue (durable or per instance) and routing key patterns.</param>
    public static IPythonScriptRunnerBuilder AddRunEventHandler<THandler>(this IPythonScriptRunnerBuilder builder, Action<RunEventSubscriptionBuilder> configure)
        where THandler : class, IRunEventHandler
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var subscription = new RunEventSubscriptionBuilder();
        configure(subscription);
        builder.Services.TryAddScoped<THandler>();
        builder.Services.AddSingleton(subscription.Build(typeof(THandler)));
        return builder;
    }

    /// <summary>Health of the broker connection: <see cref="HealthStatus.Degraded"/> when unreachable (runs wait in the outbox).</summary>
    /// <param name="builder">Health checks builder.</param>
    /// <param name="name">Check name.</param>
    /// <param name="failureStatus">Status reported while disconnected.</param>
    /// <param name="tags">Tags (e.g. <c>ready</c>).</param>
    public static IHealthChecksBuilder AddPythonScriptRunnerBroker(
        this IHealthChecksBuilder builder, string name = "psr-broker", HealthStatus failureStatus = HealthStatus.Degraded, IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<RabbitMqHealthCheck>();
        return builder.Add(new HealthCheckRegistration(name, provider => provider.GetRequiredService<RabbitMqHealthCheck>(), failureStatus, tags));
    }
}
