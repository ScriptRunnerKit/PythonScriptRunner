using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace PythonScriptRunner.EntityFrameworkCore;

/// <summary>
/// Reads <c>psr.outbox_messages</c> for the relay (spec 005): batches due now are locked with <c>FOR UPDATE SKIP LOCKED</c>,
/// so relays in several instances never publish the same row at the same time.
/// </summary>
public sealed class PsrOutbox
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly IDbContextFactory<PsrDbContext> _factory;
    private readonly PsrClock _clock;

    internal PsrOutbox(IDbContextFactory<PsrDbContext> factory, PsrClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    /// <summary>
    /// Locks up to <paramref name="batchSize"/> due messages, hands them to <paramref name="publish"/> and records the outcome:
    /// published rows get <c>published_at</c>; failed ones get <c>attempts + 1</c>, the error and a backoff (1 s doubling up to
    /// 60 s). When <paramref name="publish"/> throws (broker down) nothing changes.
    /// </summary>
    /// <param name="batchSize">Messages per batch.</param>
    /// <param name="publish">Publishes the messages and returns the outcome of each one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many messages were locked.</returns>
    public async Task<int> DispatchAsync(
        int batchSize,
        Func<IReadOnlyList<OutboxEnvelope>, CancellationToken, Task<IReadOnlyList<OutboxDelivery>>> publish,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publish);
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var now = await _clock.GetUtcNowAsync(cancellationToken).ConfigureAwait(false);
                var rows = await db.OutboxMessages.FromSql($"""
                    SELECT * FROM psr.outbox_messages
                    WHERE published_at IS NULL AND available_at <= {now}
                    ORDER BY available_at
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED
                    """).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
                if (rows.Count == 0)
                {
                    return 0;
                }

                var envelopes = rows.Select(r => new OutboxEnvelope(
                    r.Id, r.MessageType, r.Exchange, r.RoutingKey, r.Payload,
                    (JsonNode.Parse(r.Headers) as JsonObject ?? []).ToDictionary(h => h.Key, h => h.Value?.GetValue<string>() ?? string.Empty),
                    r.AvailableAt, r.Attempts)).ToList();
                var deliveries = await publish(envelopes, cancellationToken).ConfigureAwait(false);

                var published = deliveries.Where(d => d.Error is null).Select(d => d.Id).ToList();
                var publishedAt = await _clock.GetUtcNowAsync(cancellationToken).ConfigureAwait(false);
                await db.OutboxMessages.Where(m => published.Contains(m.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.PublishedAt, publishedAt).SetProperty(m => m.LastError, (string?)null), cancellationToken)
                    .ConfigureAwait(false);
                foreach (var failure in deliveries.Where(d => d.Error is not null))
                {
                    var attempts = rows.Single(r => r.Id == failure.Id).Attempts + 1;
                    var retryAt = publishedAt + Backoff(attempts);
                    var error = failure.Error!.Length <= 2000 ? failure.Error : failure.Error[..2000];
                    await db.OutboxMessages.Where(m => m.Id == failure.Id)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(m => m.Attempts, attempts)
                            .SetProperty(m => m.LastError, error)
                            .SetProperty(m => m.AvailableAt, retryAt), cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return rows.Count;
            }
        }
    }

    /// <summary>Messages not published yet (gauge <c>psr.outbox.pending</c>).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            return await db.OutboxMessages.CountAsync(m => m.PublishedAt == null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Delay before publishing again after <paramref name="attempts"/> failures: 1 s, 2 s, 4 s... up to 60 s.</summary>
    /// <param name="attempts">Failed publications so far.</param>
    public static TimeSpan Backoff(int attempts) =>
        attempts >= 7 ? MaxBackoff : TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Max(0, attempts - 1))));
}

/// <summary>A message to publish.</summary>
/// <param name="Id">Outbox row id (the AMQP <c>message_id</c>).</param>
/// <param name="MessageType">Versioned type (AMQP <c>type</c>).</param>
/// <param name="Exchange">Destination exchange.</param>
/// <param name="RoutingKey">Routing key.</param>
/// <param name="Payload">JSON body.</param>
/// <param name="Headers">Headers (e.g. <c>traceparent</c>).</param>
/// <param name="AvailableAt">When it became due.</param>
/// <param name="Attempts">Failed publications so far.</param>
public sealed record OutboxEnvelope(
    Guid Id, string MessageType, string Exchange, string RoutingKey, string Payload, IReadOnlyDictionary<string, string> Headers, DateTimeOffset AvailableAt,
    int Attempts);

/// <summary>Outcome of publishing one message.</summary>
/// <param name="Id">Outbox row id.</param>
/// <param name="Error"><c>null</c> when the broker confirmed it; otherwise why it failed.</param>
public sealed record OutboxDelivery(Guid Id, string? Error);
