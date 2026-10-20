namespace PythonScriptRunner.RabbitMQ;

/// <summary>Settings of the RabbitMQ transport.</summary>
public sealed class RabbitMqOptions
{
    /// <summary>AMQP URI (<c>amqp://user:password@host:5672/vhost</c>; <c>amqps://</c> for TLS).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Declares exchanges and queues at startup (idempotent). With <c>false</c>, startup checks that they exist and fails
    /// with a clear message when something is missing (for brokers managed by an operator). Default: <c>true</c>.
    /// </summary>
    public bool DeclareTopology { get; set; } = true;

    /// <summary>Publishes the outbox from this process (several instances can run it). Default: <c>true</c>.</summary>
    public bool RunOutboxRelay { get; set; } = true;

    /// <summary>Consumes the public <c>StartPipelineRun</c> command (<c>psr.pipeline.start</c>; D-72). Default: <c>true</c>.</summary>
    public bool EnablePublicCommands { get; set; } = true;

    /// <summary>Identifier of this instance (names its control and per-instance event queues). Default: machine name + process id.</summary>
    public string InstanceId { get; set; } = $"{Environment.MachineName}-{Environment.ProcessId}".ToLowerInvariant();

    /// <summary>How often the relay looks for due messages when the outbox is idle. Default: 250 ms (D-58).</summary>
    public TimeSpan RelayInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Messages published per relay batch. Default: 100.</summary>
    public int RelayBatchSize { get; set; } = 100;

    /// <summary>Delivery attempts of a command before it goes to <c>psr.dead-letter</c>. Default: 5.</summary>
    public int DeliveryLimit { get; set; } = 5;

    /// <summary>How long startup waits for the broker before continuing in the background. Default: 10 seconds.</summary>
    public TimeSpan StartupConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString) || !Uri.TryCreate(ConnectionString, UriKind.Absolute, out var uri) || uri.Scheme is not ("amqp" or "amqps"))
        {
            throw new PsrConfigurationException("AddRabbitMq requires o.ConnectionString with an amqp:// or amqps:// URI.");
        }

        if (string.IsNullOrWhiteSpace(InstanceId) || InstanceId.Length > 100)
        {
            throw new PsrConfigurationException("RabbitMQ InstanceId must have 1-100 chars.");
        }

        if (RelayInterval <= TimeSpan.Zero || RelayBatchSize < 1 || DeliveryLimit < 1)
        {
            throw new PsrConfigurationException("RabbitMQ relay interval, relay batch size and delivery limit must be positive.");
        }
    }
}
