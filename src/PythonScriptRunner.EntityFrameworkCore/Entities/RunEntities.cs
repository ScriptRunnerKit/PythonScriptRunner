namespace PythonScriptRunner.EntityFrameworkCore.Entities;

#pragma warning disable CS1591 // Persistence rows mirror the documented schema (psr.*); see the data mapping.

/// <summary>Row of <c>psr.runs</c> (partitioned by week on <see cref="CreatedAt"/>).</summary>
public sealed class RunEntity
{
    public Guid Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public RunKind Kind { get; set; }

    public ExecutionMode Mode { get; set; }

    public Guid? PipelineId { get; set; }

    public Guid? PipelineVersionId { get; set; }

    public string? PipelineKey { get; set; }

    public int? PipelineVersion { get; set; }

    public Guid? StageVersionId { get; set; }

    public string? StageKey { get; set; }

    public int? StageVersion { get; set; }

    public RunTrigger Trigger { get; set; }

    public required string TriggeredBy { get; set; }

    public Guid? ScheduleId { get; set; }

    /// <summary>Details of the trigger as JSON (e.g. scheduled time and missed occurrences of a schedule recovery).</summary>
    public string? TriggerDetails { get; set; }

    public RunStatus Status { get; set; }

    /// <summary>Validated inputs as JSON (files as <c>{"$artifact": id}</c>).</summary>
    public required string Inputs { get; set; }

    public string? Outputs { get; set; }

    public string? Error { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? CorrelationId { get; set; }

    public string? TraceParent { get; set; }

    public DateTimeOffset? QueuedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public DateTimeOffset? CancelRequestedAt { get; set; }

    public string? CancelRequestedBy { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public List<StepRunEntity> Steps { get; set; } = [];
}

/// <summary>Row of <c>psr.step_runs</c> (partitioned with its run).</summary>
public sealed class StepRunEntity
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public DateTimeOffset RunCreatedAt { get; set; }

    public Guid? PipelineStepId { get; set; }

    public required string StepKey { get; set; }

    public int Order { get; set; }

    public Guid? StageVersionId { get; set; }

    public required string StageKey { get; set; }

    public int StageVersion { get; set; }

    public StepStatus Status { get; set; }

    public int CurrentAttempt { get; set; }

    public string? Inputs { get; set; }

    public string? Outputs { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public List<StepAttemptEntity> Attempts { get; set; } = [];
}

/// <summary>Row of <c>psr.step_attempts</c> (partitioned with its run).</summary>
public sealed class StepAttemptEntity
{
    public Guid Id { get; set; }

    public Guid StepRunId { get; set; }

    public Guid RunId { get; set; }

    public DateTimeOffset RunCreatedAt { get; set; }

    public int Attempt { get; set; }

    public AttemptCause Cause { get; set; }

    public required string TriggeredBy { get; set; }

    public AttemptStatus Status { get; set; }

    public string? WorkerId { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public string? EnvironmentHash { get; set; }

    public int? ExitCode { get; set; }

    public ErrorKind? ErrorKind { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public int? DurationMs { get; set; }

    public int? OverheadMs { get; set; }
}

/// <summary>Row of <c>psr.attempt_logs</c> (partitioned with its run).</summary>
public sealed class AttemptLogEntity
{
    public Guid AttemptId { get; set; }

    public DateTimeOffset RunCreatedAt { get; set; }

    public LogStream Stream { get; set; }

    public required string Content { get; set; }

    public bool Truncated { get; set; }

    public long OriginalBytes { get; set; }
}

/// <summary>Row of <c>psr.artifacts</c> (metadata; partitioned with its run).</summary>
public sealed class ArtifactEntity
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public DateTimeOffset RunCreatedAt { get; set; }

    public Guid? StepRunId { get; set; }

    public ArtifactDirection Direction { get; set; }

    public required string FieldName { get; set; }

    public int? ListIndex { get; set; }

    public required string FileName { get; set; }

    public required string ContentType { get; set; }

    public long SizeBytes { get; set; }

    public required string Sha256 { get; set; }

    public DateTimeOffset ContentCreatedAt { get; set; }

    public DateTimeOffset ContentExpiresAt { get; set; }

    public DateTimeOffset? ContentDeletedAt { get; set; }
}

/// <summary>Row of <c>psr.artifact_contents</c> (blob; partitioned by day). Read and written with Npgsql streaming only.</summary>
public sealed class ArtifactContentEntity
{
    public Guid ArtifactId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public byte[] Content { get; set; } = [];
}
