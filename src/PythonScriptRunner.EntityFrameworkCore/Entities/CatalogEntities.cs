namespace PythonScriptRunner.EntityFrameworkCore.Entities;

#pragma warning disable CS1591 // Persistence rows mirror the documented schema (psr.*); see the data mapping.

/// <summary>Row of <c>psr.stages</c>.</summary>
public sealed class StageEntity
{
    public Guid Id { get; set; }

    public required string Key { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public int? LatestPublishedVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public required string UpdatedBy { get; set; }

    /// <summary>PostgreSQL <c>xmin</c> (optimistic concurrency).</summary>
    public uint RowVersion { get; set; }

    public List<StageVersionEntity> Versions { get; set; } = [];
}

/// <summary>Row of <c>psr.stage_versions</c>.</summary>
public sealed class StageVersionEntity
{
    public Guid Id { get; set; }

    public Guid StageId { get; set; }

    public StageEntity Stage { get; set; } = null!;

    public int Version { get; set; }

    public VersionStatus Status { get; set; }

    public required string Script { get; set; }

    public required string ScriptSha256 { get; set; }

    public required string PythonVersion { get; set; }

    public string? Requirements { get; set; }

    public string? LockedRequirements { get; set; }

    public string? EnvironmentHash { get; set; }

    /// <summary><c>FieldDefinition[]</c> as JSON.</summary>
    public required string InputFields { get; set; }

    /// <summary><c>FieldDefinition[]</c> as JSON.</summary>
    public required string OutputFields { get; set; }

    public string[] RequiredSecrets { get; set; } = [];

    public int TimeoutSeconds { get; set; }

    public int? MemoryLimitMb { get; set; }

    public int RetryMaxAttempts { get; set; }

    public int RetryInitialDelaySeconds { get; set; }

    public decimal RetryBackoffMultiplier { get; set; }

    public bool Idempotent { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public string? PublishedBy { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public string? ArchivedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public required string UpdatedBy { get; set; }

    public uint RowVersion { get; set; }
}

/// <summary>Row of <c>psr.pipelines</c>.</summary>
public sealed class PipelineEntity
{
    public Guid Id { get; set; }

    public required string Key { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public int? LatestPublishedVersion { get; set; }

    public int? MaxConcurrentRuns { get; set; }

    public int? ArtifactRetentionDays { get; set; }

    public int? RunRetentionDays { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public required string UpdatedBy { get; set; }

    public uint RowVersion { get; set; }

    public List<PipelineVersionEntity> Versions { get; set; } = [];
}

/// <summary>Row of <c>psr.pipeline_versions</c>.</summary>
public sealed class PipelineVersionEntity
{
    public Guid Id { get; set; }

    public Guid PipelineId { get; set; }

    public PipelineEntity Pipeline { get; set; } = null!;

    public int Version { get; set; }

    public VersionStatus Status { get; set; }

    /// <summary><c>FieldDefinition[]</c> as JSON.</summary>
    public required string InputFields { get; set; }

    /// <summary><c>PipelineOutput[]</c> as JSON.</summary>
    public required string Outputs { get; set; }

    /// <summary>Visual editor layout as JSON.</summary>
    public string? Layout { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public string? PublishedBy { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public string? ArchivedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public required string UpdatedBy { get; set; }

    public uint RowVersion { get; set; }

    public List<PipelineStepEntity> Steps { get; set; } = [];
}

/// <summary>Row of <c>psr.pipeline_steps</c>.</summary>
public sealed class PipelineStepEntity
{
    public Guid Id { get; set; }

    public Guid PipelineVersionId { get; set; }

    public int Order { get; set; }

    public required string Key { get; set; }

    public string? DisplayName { get; set; }

    public Guid StageVersionId { get; set; }

    public StageVersionEntity StageVersion { get; set; } = null!;

    /// <summary><c>InputBinding[]</c> as JSON.</summary>
    public required string Bindings { get; set; }
}

/// <summary>Row of <c>psr.schedules</c> (managed by the worker package).</summary>
public sealed class ScheduleEntity
{
    public Guid Id { get; set; }

    public Guid PipelineId { get; set; }

    public required string Name { get; set; }

    public required string Cron { get; set; }

    public required string TimeZone { get; set; }

    public int? PinnedVersion { get; set; }

    /// <summary>Fixed inputs as JSON.</summary>
    public required string Inputs { get; set; }

    public bool IsEnabled { get; set; }

    public DateTimeOffset? NextRunAt { get; set; }

    public DateTimeOffset? LastFiredAt { get; set; }

    public Guid? LastRunId { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public required string UpdatedBy { get; set; }

    public uint RowVersion { get; set; }
}
