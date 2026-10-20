using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace PythonScriptRunner.RabbitMQ;

/// <summary>
/// The RabbitMQ connection of this process. It connects on first use (the broker may be down when the host starts: queued
/// runs still go to the outbox) and makes sure the topology exists; afterwards RabbitMQ.Client recovers the connection,
/// channels and consumers automatically.
/// </summary>
public sealed class PsrBroker : IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<PsrBroker> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    internal PsrBroker(RabbitMqOptions options, ILogger<PsrBroker> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>Identifier of this instance.</summary>
    public string InstanceId => _options.InstanceId;

    /// <summary>Transport settings.</summary>
    public RabbitMqOptions Options => _options;

    /// <summary>Whether the connection is open now (it is closed while the client recovers it).</summary>
    public bool IsConnected => _connection?.IsOpen == true;

    /// <summary>The connection, connecting first when needed (the topology is declared or checked once).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="global::RabbitMQ.Client.Exceptions.BrokerUnreachableException">The broker cannot be reached (try again later).</exception>
    /// <exception cref="PsrConfigurationException">The topology is missing and must not be declared.</exception>
    public async Task<IConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is { } connection)
        {
            return connection;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                return _connection;
            }

            var factory = new ConnectionFactory
            {
                Uri = new Uri(_options.ConnectionString!),
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(2),
                ClientProvidedName = $"psr:{_options.InstanceId}",
            };
            var created = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await PsrTopology.EnsureAsync(created, _options, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await created.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            Log.Connected(_logger, created.Endpoint.ToString(), _options.DeclareTopology);
            _connection = created;
            return created;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_connection is { } connection)
        {
            try
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or global::RabbitMQ.Client.Exceptions.AlreadyClosedException or OperationCanceledException)
            {
                // Already gone.
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _lock.Dispose();
    }
}
