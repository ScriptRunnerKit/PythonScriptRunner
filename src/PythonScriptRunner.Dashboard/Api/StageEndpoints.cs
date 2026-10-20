using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace PythonScriptRunner.Dashboard.Api;

/// <summary>Stages: catalog, drafts, tests, publication, archiving, export and runs (spec 007).</summary>
internal static class StageEndpoints
{
    /// <summary>Longest publication (uv lock) before the request gives up; the draft stays intact.</summary>
    public static readonly TimeSpan PublishTimeout = TimeSpan.FromMinutes(2);

    public static void Map(RouteGroupBuilder api)
    {
        var stages = api.MapGroup("/stages").WithTags("Stages");
        stages.MapGet("/", ListAsync).RequireAuthorization(Policies.Reader).WithName("ListStages").Produces<ApiPage<StageSummary>>();
        stages.MapPost("/", CreateAsync).RequireAuthorization(Policies.Author).WithName("CreateStage")
            .Produces<StageDetail>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict);
        stages.MapGet("/{key}", GetAsync).RequireAuthorization(Policies.Reader).WithName("GetStage")
            .Produces<StageDetail>().ProducesProblem(StatusCodes.Status404NotFound);
        stages.MapPatch("/{key}", UpdateAsync).RequireAuthorization(Policies.Author).WithName("UpdateStage")
            .Produces<StageDetail>().ProducesProblem(StatusCodes.Status412PreconditionFailed).ProducesProblem(StatusCodes.Status428PreconditionRequired);
        stages.MapGet("/{key}/versions/{version:int}", GetVersionAsync).RequireAuthorization(Policies.Reader).WithName("GetStageVersion")
            .Produces<StageVersionDetail>().ProducesProblem(StatusCodes.Status404NotFound);
        stages.MapGet("/{key}/draft", GetDraftAsync).RequireAuthorization(Policies.Author).WithName("GetStageDraft")
            .Produces<StageDraft>().ProducesProblem(StatusCodes.Status404NotFound);
        stages.MapPost("/{key}/draft", CreateDraftAsync).RequireAuthorization(Policies.Author).WithName("CreateStageDraft")
            .Produces<StageDraft>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict);
        stages.MapPut("/{key}/draft", SaveDraftAsync).RequireAuthorization(Policies.Author).WithName("SaveStageDraft")
            .Produces<StageDraft>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status412PreconditionFailed);
        stages.MapDelete("/{key}/draft", DiscardDraftAsync).RequireAuthorization(Policies.Author).WithName("DiscardStageDraft")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status412PreconditionFailed);
        stages.MapPost("/{key}/draft/validate", ValidateDraftAsync).RequireAuthorization(Policies.Author).WithName("ValidateStageDraft")
            .Produces<ValidationReport>();
        stages.MapPost("/{key}/draft/test", TestDraftAsync).RequireAuthorization(Policies.Author).WithName("TestStageDraft")
            .Produces<RunHandle>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        stages.MapPost("/{key}/draft/publish", PublishAsync).RequireAuthorization(Policies.Author).WithName("PublishStage")
            .Produces<StageVersionDetail>().ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity).ProducesProblem(StatusCodes.Status504GatewayTimeout);
        stages.MapPost("/{key}/versions/{version:int}/archive", ArchiveAsync).RequireAuthorization(Policies.Author).WithName("ArchiveStageVersion")
            .Produces<StageVersionDetail>().ProducesProblem(StatusCodes.Status409Conflict);
        stages.MapDelete("/{key}", DeleteAsync).RequireAuthorization(Policies.Admin).WithName("DeleteStage")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status409Conflict);
        stages.MapGet("/{key}/versions/{version:int}/export", ExportAsync).RequireAuthorization(Policies.Reader).WithName("ExportStageVersion")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip").ProducesProblem(StatusCodes.Status404NotFound);
        stages.MapPost("/{key}/runs", RunAsync).RequireAuthorization(Policies.Executor).WithName("RunStage")
            .Produces<RunHandle>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status413PayloadTooLarge);
    }

    private static async Task<ApiPage<StageSummary>> ListAsync(
        string? search, VersionStatus? status, int? page, int? pageSize, ICatalogAdmin admin, CancellationToken cancellationToken) =>
        ApiPage<StageSummary>.From(await admin.ListStagesAsync(
            new CatalogQuery(search, page ?? 1, Math.Clamp(pageSize ?? 25, 1, 100), status), cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> CreateAsync(NewCatalogItem stage, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        await admin.CreateStageAsync(stage, await http.ActorAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        var detail = (await admin.GetStageAsync(stage.Key, cancellationToken).ConfigureAwait(false))!;
        ETags.Set(http, detail.RowVersion);
        return TypedResults.Created($"{http.Request.PathBase}{http.Request.Path}/{detail.Key}", detail);
    }

    private static async Task<IResult> GetAsync(string key, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        if (await admin.GetStageAsync(key, cancellationToken).ConfigureAwait(false) is not { } stage)
        {
            return ApiProblems.NotFound(http, $"Stage '{key}' was not found.");
        }

        ETags.Set(http, stage.RowVersion);
        return TypedResults.Ok(stage);
    }

    private static async Task<IResult> UpdateAsync(string key, CatalogItemChanges changes, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        if (ETags.IfMatch(http) is not { } rowVersion)
        {
            return ETags.Required(http);
        }

        var stage = await admin.UpdateStageAsync(key, changes, rowVersion, await http.ActorAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        ETags.Set(http, stage.RowVersion);
        return TypedResults.Ok(stage);
    }

    private static async Task<IResult> GetVersionAsync(string key, int version, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken) =>
        await admin.GetStageVersionAsync(key, version, cancellationToken).ConfigureAwait(false) is { } detail
            ? TypedResults.Ok(detail)
            : ApiProblems.NotFound(http, $"Version {version} of stage '{key}' was not found.");

    private static async Task<IResult> GetDraftAsync(string key, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        if (await admin.GetStageDraftAsync(key, cancellationToken).ConfigureAwait(false) is not { } draft)
        {
            return ApiProblems.NotFound(http, $"Stage '{key}' has no draft.");
        }

        ETags.Set(http, draft.RowVersion);
        return TypedResults.Ok(draft);
    }

    private static async Task<IResult> CreateDraftAsync(string key, NewDraftRequest request, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        var draft = await admin.CreateStageDraftFromAsync(key, request.FromVersion, await http.ActorAsync().ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        ETags.Set(http, draft.RowVersion);
        return TypedResults.Created($"{http.Request.PathBase}{http.Request.Path}", draft);
    }

    private static async Task<IResult> SaveDraftAsync(string key, StageDraftData data, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        if (ETags.IfMatch(http) is not { } rowVersion)
        {
            return ETags.Required(http);
        }

        if (await admin.GetStageDraftAsync(key, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return ApiProblems.NotFound(http, $"Stage '{key}' has no draft.");
        }

        var draft = await admin.SaveStageDraftAsync(key, current.Version, data, rowVersion, await http.ActorAsync().ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        ETags.Set(http, draft.RowVersion);
        return TypedResults.Ok(draft);
    }

    private static async Task<IResult> DiscardDraftAsync(string key, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        if (ETags.IfMatch(http) is not { } rowVersion)
        {
            return ETags.Required(http);
        }

        if (await admin.GetStageDraftAsync(key, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return ApiProblems.NotFound(http, $"Stage '{key}' has no draft.");
        }

        await admin.DiscardStageDraftAsync(key, current.Version, rowVersion, await http.ActorAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static Task<ValidationReport> ValidateDraftAsync(string key, ICatalogAdmin admin, CancellationToken cancellationToken) =>
        admin.ValidateStageDraftAsync(key, cancellationToken);

    private static async Task<IResult> TestDraftAsync(
        string key, HttpContext http, ICatalogAdmin admin, IOptions<PythonScriptRunnerOptions> options, CancellationToken cancellationToken)
    {
        var inputs = await RunInputs.ReadAsync(http, options.Value, cancellationToken).ConfigureAwait(false);
        await using (inputs.ConfigureAwait(false))
        {
            var draft = await admin.GetStageDraftAsync(key, cancellationToken).ConfigureAwait(false);
            var handle = await admin.EnqueueStageDraftTestAsync(key, inputs.Values(draft?.Data.Inputs), await http.ActorAsync().ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
            return TypedResults.Accepted(http.RunLocation(handle.RunId), handle);
        }
    }

    private static async Task<IResult> PublishAsync(string key, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        if (ETags.IfMatch(http) is not { } rowVersion)
        {
            return ETags.Required(http);
        }

        if (await admin.GetStageDraftAsync(key, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return ApiProblems.NotFound(http, $"Stage '{key}' has no draft.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PublishTimeout);
        try
        {
            return TypedResults.Ok(await admin.PublishStageAsync(key, current.Version, rowVersion, await http.ActorAsync().ConfigureAwait(false), timeout.Token)
                .ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiProblems.Result(http, StatusCodes.Status504GatewayTimeout, "PUBLISH_TIMEOUT", "The publication took too long.",
                $"Resolving the dependencies did not finish in {PublishTimeout.TotalMinutes:0} minutes; the draft was kept.");
        }
    }

    private static async Task<StageVersionDetail> ArchiveAsync(string key, int version, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken) =>
        await admin.ArchiveStageVersionAsync(key, version, await http.ActorAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> DeleteAsync(string key, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        await admin.DeleteStageAsync(key, await http.ActorAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ExportAsync(string key, int version, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        if (await admin.GetStageVersionAsync(key, version, cancellationToken).ConfigureAwait(false) is null)
        {
            return ApiProblems.NotFound(http, $"Version {version} of stage '{key}' was not found.");
        }

        return await Endpoints.ZipAsync(zip => admin.ExportStageVersionAsync(key, version, zip, cancellationToken), $"{key}@{version}.zip").ConfigureAwait(false);
    }

    private static async Task<IResult> RunAsync(
        string key, HttpContext http, IRunQueue queue, ICatalog catalog, IAuditWriter audit, IOptions<PythonScriptRunnerOptions> options,
        CancellationToken cancellationToken)
    {
        var inputs = await RunInputs.ReadAsync(http, options.Value, cancellationToken).ConfigureAwait(false);
        await using (inputs.ConfigureAwait(false))
        {
            var actor = await http.ActorAsync().ConfigureAwait(false);
            var stage = await FindAsync(catalog, key, inputs.Version, cancellationToken).ConfigureAwait(false);
            var handle = await queue.EnqueueStageAsync(key, inputs.Values(stage?.Inputs), new RunOptions
            {
                Version = inputs.Version,
                IdempotencyKey = inputs.IdempotencyKey,
                TriggeredBy = actor.Subject,
                Trigger = RunTrigger.Dashboard,
            }, cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(new AuditEntry(actor, AuditActions.RunTriggered, "Run", handle.RunId.Value, key, new JsonObject
            {
                ["kind"] = nameof(RunKind.Stage),
                ["version"] = inputs.Version,
                ["deduplicated"] = handle.Deduplicated,
            }), cancellationToken).ConfigureAwait(false);
            return TypedResults.Accepted(http.RunLocation(handle.RunId), handle);
        }
    }

    private static async Task<StageDefinition?> FindAsync(ICatalog catalog, string key, int? version, CancellationToken cancellationToken)
    {
        try
        {
            return await catalog.FindStageAsync(key, version, cancellationToken).ConfigureAwait(false);
        }
        catch (VersionNotRunnableException)
        {
            return null;
        }
    }
}
