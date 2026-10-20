namespace PythonScriptRunner.EntityFrameworkCore.Entities;

#pragma warning disable CS1591 // Persistence rows mirror the documented schema (psr.*); see the data mapping.

/// <summary>Row of <c>psr.outbox_messages</c>: written in the same transaction as the change, published by the relay.</summary>
public sealed class OutboxMessageEntity
{
    public Guid Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset AvailableAt { get; set; }

    public required string MessageType { get; set; }

    public required string Exchange { get; set; }

    public required string RoutingKey { get; set; }

    /// <summary>Body as JSON.</summary>
    public required string Payload { get; set; }

    /// <summary>Headers as JSON.</summary>
    public required string Headers { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
}

/// <summary>Row of <c>psr.idempotency_keys</c>.</summary>
public sealed class IdempotencyKeyEntity
{
    public required string Scope { get; set; }

    public required string Key { get; set; }

    public Guid RunId { get; set; }

    public DateTimeOffset RunCreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Row of <c>psr.audit_events</c> (partitioned by month).</summary>
public sealed class AuditEventEntity
{
    public Guid Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public required string Actor { get; set; }

    public required string Action { get; set; }

    public required string EntityType { get; set; }

    public Guid? EntityId { get; set; }

    public string? EntityKey { get; set; }

    public string? Details { get; set; }

    public string? TraceId { get; set; }
}
