using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client.Exceptions;

namespace PythonScriptRunner.RabbitMQ;

/// <summary>
/// Connects when the host starts: an invalid topology fails the start with a clear message (AC-12); an unreachable broker
/// does not (queued runs wait in the outbox, AC-2) and the consumers keep retrying.
/// </summary>
internal sealed class RabbitMqStartup(PsrBroker broker, ILogger<RabbitMqStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(broker.Options.StartupConnectTimeout);
        try
        {
            await broker.ConnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is BrokerUnreachableException or IOException or TimeoutException or OperationInterruptedException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Log.BrokerUnreachable(logger, ex.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Healthy while connected; otherwise the configured failure status (default <see cref="HealthStatus.Degraded"/>: the outbox keeps the runs).</summary>
internal sealed class RabbitMqHealthCheck(PsrBroker broker) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(broker.IsConnected
            ? HealthCheckResult.Healthy("Connected to RabbitMQ.")
            : new HealthCheckResult(context.Registration.FailureStatus, "RabbitMQ is unreachable; queued runs wait in the outbox."));
    }
}
