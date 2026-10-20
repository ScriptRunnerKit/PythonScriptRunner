using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PythonScriptRunner.EntityFrameworkCore.Entities;

namespace PythonScriptRunner.EntityFrameworkCore;

/// <summary>Deduplicates run requests by <c>(scope, key)</c> for <see cref="PsrEntityFrameworkOptions.IdempotencyWindow"/>.</summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Reserves the key for <paramref name="runId"/> in the current transaction of <paramref name="db"/> (a rollback releases it).
    /// Concurrent callers with the same key wait for each other: only one run holds the key.
    /// </summary>
    /// <param name="db">Context with an open transaction.</param>
    /// <param name="scope">Scope (see <see cref="IdempotencyScopes"/>).</param>
    /// <param name="key">Key given by the caller.</param>
    /// <param name="runId">Run that wants the key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>null</c> when reserved; otherwise the run that already holds the key.</returns>
    Task<RunId?> ReserveAsync(PsrDbContext db, string scope, string key, RunId runId, CancellationToken cancellationToken = default);
}

/// <summary>Scopes of idempotency keys.</summary>
public static class IdempotencyScopes
{
    /// <summary>Runs of a pipeline (including draft tests).</summary>
    /// <param name="key">Pipeline key.</param>
    public static string Pipeline(string key) => $"pipeline:{key}";

    /// <summary>Single-stage runs (including draft tests).</summary>
    /// <param name="key">Stage key.</param>
    public static string Stage(string key) => $"stage:{key}";

    /// <summary>Scope of a run kind and target.</summary>
    /// <param name="kind">Run kind.</param>
    /// <param name="key">Pipeline or stage key.</param>
    public static string For(RunKind kind, string key) => kind is RunKind.Pipeline or RunKind.PipelineTest ? Pipeline(key) : Stage(key);
}

/// <summary>Writes messages to <c>psr.outbox_messages</c> in the same transaction as the change that produced them.</summary>
public interface IOutboxWriter
{
    /// <summary>Adds the message to the context; it is stored by the caller's <c>SaveChanges</c> (and discarded by a rollback).</summary>
    /// <param name="db">Context of the change.</param>
    /// <param name="message">The message.</param>
    void Enqueue(PsrDbContext db, OutboxMessage message);
}

/// <summary>A message to publish after the transaction commits.</summary>
/// <param name="MessageType">Type (e.g. <c>psr.execute-step.v1</c>).</param>
/// <param name="Exchange">Destination exchange.</param>
/// <param name="RoutingKey">Routing key.</param>
/// <param name="Payload">Body.</param>
public sealed record OutboxMessage(string MessageType, string Exchange, string RoutingKey, JsonNode Payload)
{
    /// <summary>Message identifier (also the AMQP <c>message_id</c>); new when omitted.</summary>
    public Guid? Id { get; init; }

    /// <summary>Publish from this instant on (retry delays); now when omitted.</summary>
    public DateTimeOffset? AvailableAt { get; init; }

    /// <summary>Headers (e.g. <c>traceparent</c>).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

/// <summary>Reserves keys with <c>INSERT ... ON CONFLICT</c>: an expired key is taken over, a live one is kept.</summary>
internal sealed class IdempotencyStore(PsrEntityFrameworkOptions options, TimeProvider time) : IIdempotencyStore
{
    public async Task<RunId?> ReserveAsync(PsrDbContext db, string scope, string key, RunId runId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var now = time.GetUtcNow();
        var reserved = await ScalarAsync<Guid?>(db,
            """
            INSERT INTO psr.idempotency_keys (scope, key, run_id, run_created_at, expires_at)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (scope, key) DO UPDATE
                SET run_id = EXCLUDED.run_id, run_created_at = EXCLUDED.run_created_at, expires_at = EXCLUDED.expires_at
                WHERE psr.idempotency_keys.expires_at <= $6
            RETURNING run_id
            """,
            cancellationToken, scope, key, runId.Value, runId.CreatedAt, now + options.IdempotencyWindow, now).ConfigureAwait(false);
        if (reserved == runId.Value)
        {
            return null;
        }

        var existing = await ScalarAsync<Guid?>(db, "SELECT run_id FROM psr.idempotency_keys WHERE scope = $1 AND key = $2", cancellationToken, scope, key)
            .ConfigureAwait(false);
        return existing is { } id ? new RunId(id) : null;
    }

    /// <summary>Runs a scalar command on the connection and transaction of the context.</summary>
    internal static async Task<T?> ScalarAsync<T>(PsrDbContext db, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection(), db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction);
        await using (command.ConfigureAwait(false))
        {
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(new NpgsqlParameter { Value = parameter });
            }

            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is null or DBNull ? default : (T)value;
        }
    }
}

/// <summary>Adds outbox rows to the caller's context.</summary>
internal sealed class OutboxWriter(TimeProvider time) : IOutboxWriter
{
    public void Enqueue(PsrDbContext db, OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(message);
        var now = time.GetUtcNow();
        db.OutboxMessages.Add(new OutboxMessageEntity
        {
            Id = message.Id ?? Guid.CreateVersion7(now),
            CreatedAt = now,
            AvailableAt = message.AvailableAt ?? now,
            MessageType = message.MessageType,
            Exchange = message.Exchange,
            RoutingKey = message.RoutingKey,
            Payload = message.Payload.ToJsonString(),
            Headers = new JsonObject(message.Headers.Select(h => KeyValuePair.Create(h.Key, (JsonNode?)JsonValue.Create(h.Value)))).ToJsonString(),
        });
    }
}
