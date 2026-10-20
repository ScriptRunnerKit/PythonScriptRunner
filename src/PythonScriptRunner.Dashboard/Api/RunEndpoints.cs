using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PythonScriptRunner.EntityFrameworkCore;

namespace PythonScriptRunner.Dashboard.Api;

/// <summary>Runs, logs, files and the overview (spec 007).</summary>
internal static class RunEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/overview", OverviewAsync).RequireAuthorization(Policies.Reader).WithTags("Runs").WithName("GetOverview").Produces<OverviewResponse>();

        var runs = api.MapGroup("/runs").WithTags("Runs");
        runs.MapGet("/", ListAsync).RequireAuthorization(Policies.Reader).WithName("ListRuns")
            .Produces<KeysetPage<RunSummary>>().ProducesProblem(StatusCodes.Status400BadRequest);
        runs.MapGet("/{id}", GetAsync).RequireAuthorization(Policies.Reader).WithName("GetRun")
            .Produces<RunSnapshot>().ProducesProblem(StatusCodes.Status404NotFound);
        runs.MapGet("/{id}/attempts/{attemptId}/logs/{stream}", LogAsync).RequireAuthorization(Policies.Reader).WithName("GetAttemptLog")
            .Produces<string>(StatusCodes.Status200OK, "text/plain").ProducesProblem(StatusCodes.Status404NotFound);
        runs.MapPost("/{id}/cancel", CancelAsync).RequireAuthorization(Policies.Executor).WithName("CancelRun")
            .Produces(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status409Conflict);
        runs.MapPost("/{id}/reprocess", ReprocessAsync).RequireAuthorization(Policies.Executor).WithName("ReprocessRun")
            .Produces<RunHandle>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet("/artifacts/{id}", DownloadAsync).RequireAuthorization(Policies.Reader).WithTags("Runs").WithName("DownloadArtifact")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status410Gone);
    }

    private static Task<OverviewResponse> OverviewAsync(OverviewQuery overview, CancellationToken cancellationToken) => overview.GetAsync(cancellationToken);

    private static Task<KeysetPage<RunSummary>> ListAsync([AsParameters] RunListQuery query, IRunManager runs, CancellationToken cancellationToken) =>
        runs.ListAsync(new RunQuery
        {
            PipelineKey = query.PipelineKey,
            StageKey = query.StageKey,
            Kind = query.Kind,
            Status = query.Status,
            Trigger = query.Trigger,
            From = query.From,
            To = query.To,
            Cursor = query.Cursor,
            Limit = query.Limit ?? 50,
        }, cancellationToken);

    private static async Task<IResult> GetAsync(RunId id, HttpContext http, IRunManager runs, CancellationToken cancellationToken) =>
        await runs.GetAsync(id, cancellationToken).ConfigureAwait(false) is { } run
            ? TypedResults.Ok(run)
            : ApiProblems.NotFound(http, $"Run {id} was not found.");

    private static async Task<IResult> LogAsync(
        RunId id, AttemptId attemptId, string stream, HttpContext http, IRunManager runs, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<LogStream>(stream, ignoreCase: true, out var which) || !Enum.IsDefined(which) || int.TryParse(stream, out _)
            || await runs.GetAttemptLogAsync(id, attemptId, which, cancellationToken).ConfigureAwait(false) is not { } log)
        {
            return ApiProblems.NotFound(http, $"No {stream} log for attempt {attemptId} of run {id}.");
        }

        http.Response.Headers["X-PSR-Log-Truncated"] = log.Truncated ? "true" : "false";
        http.Response.Headers["X-PSR-Log-Original-Bytes"] = log.OriginalBytes.ToString(CultureInfo.InvariantCulture);
        return TypedResults.Text(log.Content, "text/plain; charset=utf-8");
    }

    private static async Task<IResult> CancelAsync(RunId id, HttpContext http, IRunManager runs, CancellationToken cancellationToken)
    {
        await runs.CancelAsync(id, await http.ActorAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return TypedResults.Accepted(http.RunLocation(id));
    }

    private static async Task<IResult> ReprocessAsync(RunId id, HttpContext http, IRunManager runs, CancellationToken cancellationToken)
    {
        var run = await runs.ReprocessAsync(id, await http.ActorAsync().ConfigureAwait(false), ExecutionMode.Async, cancellationToken).ConfigureAwait(false);
        return TypedResults.Accepted(http.RunLocation(id), new RunHandle(run.Id, run.Status, Deduplicated: false));
    }

    /// <summary>Streams a file as an attachment; the content is never rendered by the browser (sandboxed, no sniffing).</summary>
    private static async Task<IResult> DownloadAsync(ArtifactId id, HttpContext http, IArtifactStore store, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } artifact)
        {
            return ApiProblems.NotFound(http, $"File {id} was not found.");
        }

        var content = await store.OpenReadAsync(id, cancellationToken).ConfigureAwait(false);
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "sandbox";
        return TypedResults.File(content, artifact.ContentType, artifact.FileName);
    }
}

/// <summary>Filters of <c>GET /runs</c> (camelCase in the query, like the rest of the API).</summary>
internal sealed record RunListQuery(
    [FromQuery(Name = "pipelineKey")] string? PipelineKey,
    [FromQuery(Name = "stageKey")] string? StageKey,
    [FromQuery(Name = "kind")] RunKind? Kind,
    [FromQuery(Name = "status")] RunStatus? Status,
    [FromQuery(Name = "trigger")] RunTrigger? Trigger,
    [FromQuery(Name = "from")] DateTimeOffset? From,
    [FromQuery(Name = "to")] DateTimeOffset? To,
    [FromQuery(Name = "cursor")] string? Cursor,
    [FromQuery(Name = "limit")] int? Limit);

/// <summary>Computes <c>GET /overview</c> (D-115).</summary>
internal sealed class OverviewQuery(IDbContextFactory<PsrDbContext> factory, IRunManager runs, TimeProvider time)
{
    private static readonly RunStatus[] InProgressStatuses = [RunStatus.Pending, RunStatus.Queued, RunStatus.Running, RunStatus.Cancelling];

    public async Task<OverviewResponse> GetAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var dayAgo = now.AddHours(-24);
        var twoDaysAgo = now.AddHours(-48);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var counts = await db.Runs.AsNoTracking()
                .Where(r => r.CreatedAt >= twoDaysAgo && r.CreatedAt < now)
                .GroupBy(r => new { Recent = r.CreatedAt >= dayAgo, r.Status })
                .Select(g => new { g.Key.Recent, g.Key.Status, Count = g.Count() })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var upcoming = await db.Schedules.AsNoTracking()
                .Where(s => s.IsEnabled && s.NextRunAt != null)
                .OrderBy(s => s.NextRunAt)
                .Take(5)
                .Join(db.Pipelines, s => s.PipelineId, p => p.Id, (s, p) => new { s.Id, s.Name, p.Key, NextRunAt = s.NextRunAt!.Value })
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            // Runs created in this very instant are included; the window stays within the 90 days the history keeps.
            var until = now.AddMinutes(1);
            var inProgress = new List<RunSummary>();
            foreach (var status in InProgressStatuses)
            {
                var page = await runs.ListAsync(new RunQuery { Status = status, From = until.AddDays(-90), To = until, Limit = 50 }, cancellationToken)
                    .ConfigureAwait(false);
                inProgress.AddRange(page.Items);
            }

            var failures = await runs.ListAsync(new RunQuery { Status = RunStatus.Failed, From = now.AddDays(-7), To = until, Limit = 10 }, cancellationToken)
                .ConfigureAwait(false);
            return new OverviewResponse(
                now,
                ByStatus(counts.Where(c => c.Recent).Select(c => (c.Status, c.Count))),
                ByStatus(counts.Where(c => !c.Recent).Select(c => (c.Status, c.Count))),
                [.. inProgress.OrderByDescending(r => r.CreatedAt).Take(50)],
                failures.Items,
                [.. upcoming.OrderBy(s => s.NextRunAt).Select(s => new UpcomingSchedule(new ScheduleId(s.Id), s.Name, s.Key, s.NextRunAt))]);
        }
    }

    /// <summary>Every status, zero when absent (the panel draws all counters).</summary>
    private static Dictionary<RunStatus, int> ByStatus(IEnumerable<(RunStatus Status, int Count)> counts)
    {
        var result = Enum.GetValues<RunStatus>().ToDictionary(s => s, _ => 0);
        foreach (var (status, count) in counts)
        {
            result[status] += count;
        }

        return result;
    }
}
