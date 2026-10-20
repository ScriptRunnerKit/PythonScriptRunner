using System.Text.Json.Nodes;

namespace PythonScriptRunner;

/// <summary>Records human actions (publish, archive, delete, trigger, cancel, reprocess...). Automatic callers are not audited.</summary>
public interface IAuditWriter
{
    /// <summary>Writes an event when <see cref="AuditEntry.Actor"/> is human; does nothing otherwise.</summary>
    /// <param name="entry">The event.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>Reads audit events.</summary>
public interface IAuditReader
{
    /// <summary>Lists events newest first inside a time window (default: last 24 hours; at most 90 days).</summary>
    /// <param name="query">Filters, window and cursor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<KeysetPage<AuditEvent>> ListAsync(AuditQuery query, CancellationToken cancellationToken = default);
}

/// <summary>An action to audit.</summary>
/// <param name="Actor">Who acted.</param>
/// <param name="Action">What was done (see <see cref="AuditActions"/>).</param>
/// <param name="EntityType"><c>Stage</c>, <c>StageVersion</c>, <c>Pipeline</c>, <c>PipelineVersion</c>, <c>Schedule</c>, <c>Run</c> or <c>Settings</c>.</param>
/// <param name="EntityId">Identifier of the entity.</param>
/// <param name="EntityKey">Readable reference (e.g. <c>html-to-pdf@3</c>).</param>
/// <param name="Details">Diff (<c>{field: [before, after]}</c>) or action parameters.</param>
public sealed record AuditEntry(Actor Actor, string Action, string EntityType, Guid? EntityId, string? EntityKey, JsonObject? Details = null);

/// <summary>A stored audit event.</summary>
/// <param name="Id">Event identifier.</param>
/// <param name="OccurredAt">When it happened.</param>
/// <param name="Actor">Subject of the user.</param>
/// <param name="Action">What was done.</param>
/// <param name="EntityType">Entity type.</param>
/// <param name="EntityId">Entity identifier.</param>
/// <param name="EntityKey">Readable reference.</param>
/// <param name="Details">Diff or parameters.</param>
/// <param name="TraceId">OpenTelemetry trace.</param>
public sealed record AuditEvent(
    Guid Id, DateTimeOffset OccurredAt, string Actor, string Action, string EntityType, Guid? EntityId, string? EntityKey, JsonObject? Details, string? TraceId);

/// <summary>Filters of an audit list.</summary>
public sealed record AuditQuery
{
    /// <summary>Start of the window (inclusive); default: <see cref="To"/> minus 24 hours.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>End of the window (exclusive); default: now.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Only events of this user.</summary>
    public string? Actor { get; init; }

    /// <summary>Only events of this entity type.</summary>
    public string? EntityType { get; init; }

    /// <summary>Cursor returned by the previous page.</summary>
    public string? Cursor { get; init; }

    /// <summary>Items per page (1-100). Default: 50.</summary>
    public int Limit { get; init; } = 50;
}

/// <summary>Audited actions.</summary>
public static class AuditActions
{
#pragma warning disable CS1591 // The names are self-explanatory.
    public const string StageCreated = nameof(StageCreated);
    public const string StageUpdated = nameof(StageUpdated);
    public const string StageDraftCreated = nameof(StageDraftCreated);
    public const string StageDraftSaved = nameof(StageDraftSaved);
    public const string StageDraftDiscarded = nameof(StageDraftDiscarded);
    public const string StagePublished = nameof(StagePublished);
    public const string StageVersionArchived = nameof(StageVersionArchived);
    public const string StageDeleted = nameof(StageDeleted);
    public const string StageTested = nameof(StageTested);
    public const string PipelineCreated = nameof(PipelineCreated);
    public const string PipelineUpdated = nameof(PipelineUpdated);
    public const string PipelineDraftCreated = nameof(PipelineDraftCreated);
    public const string PipelineDraftSaved = nameof(PipelineDraftSaved);
    public const string PipelineDraftDiscarded = nameof(PipelineDraftDiscarded);
    public const string PipelinePublished = nameof(PipelinePublished);
    public const string PipelineVersionArchived = nameof(PipelineVersionArchived);
    public const string PipelineDeleted = nameof(PipelineDeleted);
    public const string PipelineSettingsChanged = nameof(PipelineSettingsChanged);
    public const string PipelineTested = nameof(PipelineTested);
    public const string ScheduleCreated = nameof(ScheduleCreated);
    public const string ScheduleUpdated = nameof(ScheduleUpdated);
    public const string ScheduleDeleted = nameof(ScheduleDeleted);
    public const string ScheduleEnabled = nameof(ScheduleEnabled);
    public const string ScheduleDisabled = nameof(ScheduleDisabled);
    public const string RunTriggered = nameof(RunTriggered);
    public const string RunCancelled = nameof(RunCancelled);
    public const string RunReprocessed = nameof(RunReprocessed);
    public const string CatalogImported = nameof(CatalogImported);
#pragma warning restore CS1591
}
