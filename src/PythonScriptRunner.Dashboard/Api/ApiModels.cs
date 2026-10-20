using System.Text.Json.Nodes;
using PythonScriptRunner.EntityFrameworkCore;

namespace PythonScriptRunner.Dashboard.Api;

/// <summary>One page of a catalog list.</summary>
/// <typeparam name="T">Item type.</typeparam>
/// <param name="Items">Items of the page.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Items per page.</param>
/// <param name="Total">Number of items in every page.</param>
internal sealed record ApiPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total)
{
    public static ApiPage<T> From(Page<T> page) => new(page.Items, page.PageNumber, page.PageSize, page.Total);
}

/// <summary>How the panel signs in (<c>GET /auth</c>); never lists users.</summary>
/// <param name="Mode">Access mode.</param>
/// <param name="LoginUrl">Host login page (host authentication).</param>
/// <param name="LogoutUrl">Host logout page (host authentication).</param>
/// <param name="LocalRequests">Whether local requests enter without login.</param>
internal sealed record AuthInfo(DashboardAccessMode Mode, string? LoginUrl, string? LogoutUrl, bool LocalRequests);

/// <summary>Fixed login credentials.</summary>
/// <param name="Name">User name.</param>
/// <param name="Password">Password.</param>
internal sealed record LoginRequest(string? Name, string? Password);

/// <summary>The current user (<c>GET /me</c>).</summary>
/// <param name="Subject">Recorded in the audit.</param>
/// <param name="Name">Display name.</param>
/// <param name="Role">Role (<c>null</c>: authenticated by the host without a dashboard role).</param>
/// <param name="Roles">Every role the user has (cumulative).</param>
/// <param name="Mode">How the user was identified.</param>
internal sealed record MeResponse(string Subject, string Name, DashboardRole? Role, IReadOnlyList<DashboardRole> Roles, DashboardAccessMode Mode);

/// <summary>Copies a version into a new draft.</summary>
/// <param name="FromVersion">Version to copy.</param>
internal sealed record NewDraftRequest(int FromVersion);

/// <summary>Next occurrences of a cron expression.</summary>
/// <param name="Cron">Cron expression (5 or 6 fields).</param>
/// <param name="TimeZone">IANA time zone (default <c>America/Sao_Paulo</c>).</param>
/// <param name="Count">How many (1-20; default 5).</param>
internal sealed record SchedulePreviewRequest(string Cron, string? TimeZone, int? Count);

/// <summary>Run counters and lists of <c>GET /overview</c> (D-115).</summary>
/// <param name="GeneratedAt">When it was computed.</param>
/// <param name="Last24Hours">Runs created in the last 24 hours, by status.</param>
/// <param name="Previous24Hours">Runs created in the 24 hours before, by status.</param>
/// <param name="InProgress">Runs pending, queued, running or cancelling (newest first, up to 50).</param>
/// <param name="RecentFailures">The 10 latest failed runs (last 7 days).</param>
/// <param name="UpcomingSchedules">The 5 next schedule firings.</param>
internal sealed record OverviewResponse(
    DateTimeOffset GeneratedAt,
    IReadOnlyDictionary<RunStatus, int> Last24Hours,
    IReadOnlyDictionary<RunStatus, int> Previous24Hours,
    IReadOnlyList<RunSummary> InProgress,
    IReadOnlyList<RunSummary> RecentFailures,
    IReadOnlyList<UpcomingSchedule> UpcomingSchedules);

/// <summary>A next schedule firing.</summary>
/// <param name="Id">Schedule identifier.</param>
/// <param name="Name">Schedule name.</param>
/// <param name="PipelineKey">Scheduled pipeline.</param>
/// <param name="NextRunAt">Next firing (UTC).</param>
internal sealed record UpcomingSchedule(ScheduleId Id, string Name, string PipelineKey, DateTimeOffset NextRunAt);

/// <summary>Status of a secret declared by stages (never its value).</summary>
/// <param name="Name">Secret name.</param>
/// <param name="DeclaredBy">Stage versions that declare it (<c>key@version</c>).</param>
/// <param name="Resolved">Whether the secret provider returns a value.</param>
internal sealed record SecretStatus(string Name, IReadOnlyList<string> DeclaredBy, bool Resolved);

/// <summary>Read-only settings (<c>GET /admin/settings</c>).</summary>
/// <param name="Retention">Global retention.</param>
/// <param name="Limits">Limits of runs and uploads.</param>
/// <param name="Access">Access mode of the dashboard.</param>
/// <param name="Versions">Versions of the components.</param>
internal sealed record SettingsResponse(RetentionSettings Retention, LimitSettings Limits, AuthInfo Access, IReadOnlyDictionary<string, string> Versions);

/// <summary>Global retention in days.</summary>
/// <param name="ArtifactDays">File contents.</param>
/// <param name="RunDays">Runs.</param>
/// <param name="AuditDays">Audit events.</param>
internal sealed record RetentionSettings(double ArtifactDays, double RunDays, double AuditDays)
{
    public static RetentionSettings From(PsrRetentionOptions retention) =>
        new(retention.Artifacts.TotalDays, retention.Runs.TotalDays, retention.Audit.TotalDays);
}

/// <summary>Limits of runs and uploads.</summary>
/// <param name="MaxFileBytes">Largest input or output file.</param>
/// <param name="MaxRunFilesBytes">Largest total of the files of a run.</param>
/// <param name="MaxLogBytes">Log tail kept per stream.</param>
/// <param name="DefaultTimeout">Default timeout of an attempt.</param>
/// <param name="DefaultMemoryLimitMb">Default memory limit.</param>
/// <param name="MaxConcurrentProcesses">Python processes per instance.</param>
/// <param name="DefaultPythonVersion">Default Python version.</param>
internal sealed record LimitSettings(
    long MaxFileBytes, long MaxRunFilesBytes, int MaxLogBytes, TimeSpan DefaultTimeout, int DefaultMemoryLimitMb, int MaxConcurrentProcesses, string DefaultPythonVersion);

/// <summary>Field types for the editor (<c>GET /meta/field-types</c>).</summary>
/// <param name="Types">The 12 types and their widgets.</param>
/// <param name="Compatibility">Which source types can feed which target types (representative types; see <paramref name="Rules"/>).</param>
/// <param name="Rules">Rules that depend on the declaration (enum values, table columns, file content types, list items).</param>
internal sealed record FieldTypesResponse(IReadOnlyList<FieldTypeInfo> Types, IReadOnlyDictionary<string, IReadOnlyList<string>> Compatibility, IReadOnlyList<string> Rules);

/// <summary>One field type.</summary>
/// <param name="Name">Wire name (<c>number</c>, <c>list</c>...).</param>
/// <param name="Scalar">Whether it is a scalar.</param>
/// <param name="InputWidget">Widget of the input form.</param>
/// <param name="OutputWidget">How outputs are shown.</param>
/// <param name="AllowsDefault">Whether a default value can be declared.</param>
internal sealed record FieldTypeInfo(string Name, bool Scalar, string InputWidget, string OutputWidget, bool AllowsDefault);

/// <summary>Body of a run without files (JSON alternative to multipart).</summary>
/// <param name="Inputs">Inputs.</param>
/// <param name="Version">Version to run (default: latest published).</param>
/// <param name="IdempotencyKey">Deduplicates repeated requests for 24 hours.</param>
internal sealed record RunRequest(JsonObject? Inputs, int? Version, string? IdempotencyKey);

/// <summary>Real-time update of a run (<c>RunUpdated</c>).</summary>
/// <param name="Run">The run.</param>
/// <param name="EventType">Event that changed it.</param>
/// <param name="CurrentStep">Step running or last finished.</param>
internal sealed record RunUpdate(RunSummary Run, string EventType, string? CurrentStep);

/// <summary>Real-time update of a step (<c>StepUpdated</c>).</summary>
/// <param name="RunId">Run.</param>
/// <param name="StepKey">Step key.</param>
/// <param name="Order">1-based position.</param>
/// <param name="Status">Status of the step.</param>
/// <param name="Attempt">Attempt number.</param>
/// <param name="StartedAt">Start time.</param>
/// <param name="FinishedAt">End time.</param>
/// <param name="ErrorKind">Error kind of a failed step.</param>
internal sealed record StepUpdate(
    RunId RunId, string StepKey, int Order, StepStatus Status, int Attempt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, ErrorKind? ErrorKind);
