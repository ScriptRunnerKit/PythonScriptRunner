using System.Text.Json.Nodes;

namespace PythonScriptRunner;

/// <summary>Settings of a cron schedule of a pipeline.</summary>
public sealed record ScheduleData
{
    /// <summary>Default IANA time zone of schedules.</summary>
    public const string DefaultTimeZone = "America/Sao_Paulo";

    /// <summary>Display name (1-200 chars).</summary>
    public required string Name { get; init; }

    /// <summary>Cron expression with 5 fields (minute precision) or 6 fields (with seconds).</summary>
    public required string Cron { get; init; }

    /// <summary>IANA time zone in which the expression is evaluated (daylight saving time handled).</summary>
    public string TimeZone { get; init; } = DefaultTimeZone;

    /// <summary>Version to run; <c>null</c> runs the latest published version at each firing.</summary>
    public int? PinnedVersion { get; init; }

    /// <summary>Fixed inputs of the pipeline (no files).</summary>
    public JsonObject Inputs { get; init; } = [];

    /// <summary>Whether the schedule fires.</summary>
    public bool IsEnabled { get; init; } = true;
}

/// <summary>A stored schedule.</summary>
public sealed record ScheduleDetail
{
    /// <summary>Identifier.</summary>
    public required ScheduleId Id { get; init; }

    /// <summary>Scheduled pipeline.</summary>
    public required string PipelineKey { get; init; }

    /// <summary>Settings.</summary>
    public required ScheduleData Data { get; init; }

    /// <summary>Next firing (UTC); <c>null</c> when disabled.</summary>
    public DateTimeOffset? NextRunAt { get; init; }

    /// <summary>Last firing.</summary>
    public DateTimeOffset? LastFiredAt { get; init; }

    /// <summary>Run created by the last successful firing.</summary>
    public RunId? LastRunId { get; init; }

    /// <summary>Why the last firing was rejected (cleared by the next successful one).</summary>
    public string? LastError { get; init; }

    /// <summary>Row version for optimistic concurrency.</summary>
    public required uint RowVersion { get; init; }

    /// <summary>Who created and last changed it.</summary>
    public required Authorship Authorship { get; init; }
}

/// <summary>Filter and page of a schedule list.</summary>
/// <param name="PipelineKey">Only schedules of this pipeline.</param>
/// <param name="PageNumber">1-based page number.</param>
/// <param name="PageSize">Items per page (1-100).</param>
public sealed record ScheduleQuery(string? PipelineKey = null, int PageNumber = 1, int PageSize = 25);
