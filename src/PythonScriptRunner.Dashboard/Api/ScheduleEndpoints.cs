using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PythonScriptRunner.EntityFrameworkCore;

namespace PythonScriptRunner.Dashboard.Api;

/// <summary>Cron schedules of pipelines (spec 007; creation is under <c>/pipelines/{key}/schedules</c>).</summary>
internal static class ScheduleEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var schedules = api.MapGroup("/schedules").WithTags("Schedules");
        schedules.MapGet("/", ListAsync).RequireAuthorization(Policies.Reader).WithName("ListSchedules").Produces<ApiPage<ScheduleDetail>>();
        schedules.MapPut("/{id}", UpdateAsync).RequireAuthorization(Policies.Author).WithName("UpdateSchedule")
            .Produces<ScheduleDetail>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status412PreconditionFailed);
        schedules.MapDelete("/{id}", DeleteAsync).RequireAuthorization(Policies.Author).WithName("DeleteSchedule")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status412PreconditionFailed);
        schedules.MapPost("/{id}/enable", (ScheduleId id, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken) =>
                SetEnabledAsync(id, true, http, admin, cancellationToken))
            .RequireAuthorization(Policies.Author).WithName("EnableSchedule").Produces<ScheduleDetail>();
        schedules.MapPost("/{id}/disable", (ScheduleId id, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken) =>
                SetEnabledAsync(id, false, http, admin, cancellationToken))
            .RequireAuthorization(Policies.Author).WithName("DisableSchedule").Produces<ScheduleDetail>();
        schedules.MapPost("/preview", Preview).RequireAuthorization(Policies.Reader).WithName("PreviewSchedule")
            .Produces<IReadOnlyList<ScheduleOccurrence>>().ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static async Task<ApiPage<ScheduleDetail>> ListAsync(
        string? pipelineKey, int? page, int? pageSize, ICatalogAdmin admin, CancellationToken cancellationToken) =>
        ApiPage<ScheduleDetail>.From(await admin.ListSchedulesAsync(
            new ScheduleQuery(pipelineKey, page ?? 1, Math.Clamp(pageSize ?? 25, 1, 100)), cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> UpdateAsync(ScheduleId id, ScheduleData schedule, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        if (ETags.IfMatch(http) is not { } rowVersion)
        {
            return ETags.Required(http);
        }

        var updated = await admin.UpdateScheduleAsync(id, schedule, rowVersion, await http.ActorAsync().ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        ETags.Set(http, updated.RowVersion);
        return TypedResults.Ok(updated);
    }

    private static async Task<IResult> DeleteAsync(ScheduleId id, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        if (ETags.IfMatch(http) is not { } rowVersion)
        {
            return ETags.Required(http);
        }

        var current = await admin.GetScheduleAsync(id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return ApiProblems.NotFound(http, $"Schedule {id} was not found.");
        }

        if (current.RowVersion != rowVersion)
        {
            throw new ConcurrencyConflictException($"Schedule '{current.Data.Name}' was changed by someone else.", current);
        }

        await admin.DeleteScheduleAsync(id, await http.ActorAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    /// <summary>Enables or disables; <c>If-Match</c> is optional here (a switch, not an edit).</summary>
    private static async Task<IResult> SetEnabledAsync(ScheduleId id, bool enabled, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        var rowVersion = ETags.IfMatch(http);
        if (rowVersion is null)
        {
            if (await admin.GetScheduleAsync(id, cancellationToken).ConfigureAwait(false) is not { } current)
            {
                return ApiProblems.NotFound(http, $"Schedule {id} was not found.");
            }

            rowVersion = current.RowVersion;
        }

        var schedule = await admin.SetScheduleEnabledAsync(id, enabled, rowVersion.Value, await http.ActorAsync().ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        ETags.Set(http, schedule.RowVersion);
        return TypedResults.Ok(schedule);
    }

    private static IReadOnlyList<ScheduleOccurrence> Preview(SchedulePreviewRequest request, TimeProvider time) =>
        SchedulePreview.Next(request.Cron, request.TimeZone ?? ScheduleData.DefaultTimeZone, time.GetUtcNow(), request.Count ?? 5);
}
