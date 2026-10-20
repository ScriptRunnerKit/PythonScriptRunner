using System.Text;
using RabbitMQ.Client;

namespace PythonScriptRunner.Testing;

/// <summary>Talks to the broker directly, like a system that is not .NET (or a misbehaving one) would.</summary>
public sealed class RawBroker : IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;

    private RawBroker(IConnection connection, IChannel channel)
    {
        _connection = connection;
        _channel = channel;
    }

    public static async Task<RawBroker> ConnectAsync(string uri)
    {
        var connection = await new ConnectionFactory { Uri = new Uri(uri) }.CreateConnectionAsync(TestContext.Current.CancellationToken);
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), TestContext.Current.CancellationToken);
        return new RawBroker(connection, channel);
    }

    public async Task PublishAsync(string exchange, string routingKey, string body, string? type, string? messageId = null)
    {
        var properties = new BasicProperties
        {
            MessageId = messageId ?? Guid.NewGuid().ToString(),
            Type = type,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
        };
        await _channel.BasicPublishAsync(exchange, routingKey, mandatory: true, properties, Encoding.UTF8.GetBytes(body), TestContext.Current.CancellationToken);
    }

    /// <summary>Reads one message of a queue (without acknowledging others).</summary>
    public async Task<string?> GetAsync(string queue)
    {
        var result = await _channel.BasicGetAsync(queue, autoAck: true, TestContext.Current.CancellationToken);
        return result is null ? null : Encoding.UTF8.GetString(result.Body.Span);
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
