using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace PythonScriptRunner.Dashboard.Api;

/// <summary>Pipelines: catalog, drafts, validation, tests, publication, settings, export, import, runs and schedules (spec 007).</summary>
internal static class PipelineEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var pipelines = api.MapGroup("/pipelines").WithTags("Pipelines");
        pipelines.MapGet("/", ListAsync).RequireAuthorization(Policies.Reader).WithName("ListPipelines").Produces<ApiPage<PipelineSummary>>();
        pipelines.MapPost("/", CreateAsync).RequireAuthorization(Policies.Author).WithName("CreatePipeline")
            .Produces<PipelineDetail>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict);
        pipelines.MapPost("/validate", ValidateAsync).RequireAuthorization(Policies.Author).WithName("ValidatePipeline").Produces<ValidationReport>();
        pipelines.MapGet("/{key}", GetAsync).RequireAuthorization(Policies.Reader).WithName("GetPipeline")
            .Produces<PipelineDetail>().ProducesProblem(StatusCodes.Status404NotFound);
        pipelines.MapGet("/{key}/versions/{version:int}", GetVersionAsync).RequireAuthorization(Policies.Reader).WithName("GetPipelineVersion")
            .Produces<PipelineVersionDetail>().ProducesProblem(StatusCodes.Status404NotFound);
        pipelines.MapGet("/{key}/draft", GetDraftAsync).RequireAuthorization(Policies.Author).WithName("GetPipelineDraft")
            .Produces<PipelineDraft>().ProducesProblem(StatusCodes.Status404NotFound);
        pipelines.MapPost("/{key}/draft", CreateDraftAsync).RequireAuthorization(Policies.Author).WithName("CreatePipelineDraft")
            .Produces<PipelineDraft>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict);
        pipelines.MapPut("/{key}/draft", SaveDraftAsync).RequireAuthorization(Policies.Author).WithName("SavePipelineDraft")
            .Produces<PipelineDraft>().ProducesProblem(StatusCodes.Status412PreconditionFailed).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        pipelines.MapDelete("/{key}/draft", DiscardDraftAsync).RequireAuthorization(Policies.Author).WithName("DiscardPipelineDraft")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status412PreconditionFailed);
        pipelines.MapPost("/{key}/draft/test", TestDraftAsync).RequireAuthorization(Policies.Author).WithName("TestPipelineDraft")
            .Produces<RunHandle>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        pipelines.MapPost("/{key}/draft/publish", PublishAsync).RequireAuthorization(Policies.Author).WithName("PublishPipeline")
            .Produces<PipelinePublication>().ProducesProblem(StatusCodes.Status412PreconditionFailed).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        pipelines.MapPost("/{key}/versions/{version:int}/archive", ArchiveAsync).RequireAuthorization(Policies.Author).WithName("ArchivePipelineVersion")
            .Produces<PipelineVersionDetail>().ProducesProblem(StatusCodes.Status409Conflict);
        pipelines.MapDelete("/{key}", DeleteAsync).RequireAuthorization(Policies.Admin).WithName("DeletePipeline")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status409Conflict);
        pipelines.MapPatch("/{key}/settings", UpdateSettingsAsync).RequireAuthorization(Policies.Author).WithName("UpdatePipelineSettings")
            .Produces<PipelineDetail>().ProducesProblem(StatusCodes.Status412PreconditionFailed);
        pipelines.MapPatch("/{key}/retention", UpdateRetentionAsync).RequireAuthorization(Policies.Admin).WithName("UpdatePipelineRetention")
            .Produces<PipelineDetail>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status412PreconditionFailed);
        pipelines.MapGet("/{key}/versions/{version:int}/export", ExportAsync).RequireAuthorization(Policies.Reader).WithName("ExportPipelineVersion")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip").ProducesProblem(StatusCodes.Status404NotFound);
        pipelines.MapPost("/{key}/runs", RunAsync).RequireAuthorization(Policies.Executor).WithName("RunPipeline")
            .Produces<RunHandle>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status413PayloadTooLarge);
        pipelines.MapPost("/{key}/schedules", CreateScheduleAsync).RequireAuthorization(Policies.Author).WithTags("Schedules").WithName("CreateSchedule")
            .Produces<ScheduleDetail>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapPost("/import", ImportAsync).RequireAuthorization(Policies.Author).WithTags("Pipelines").WithName("Import")
            .Produces<ImportReport>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status413PayloadTooLarge);
    }

    private static async Task<ApiPage<PipelineSummary>> ListAsync(
        string? search, VersionStatus? status, int? page, int? pageSize, ICatalogAdmin admin, CancellationToken cancellationToken) =>
        ApiPage<PipelineSummary>.From(await admin.ListPipelinesAsync(
            new CatalogQuery(search, page ?? 1, Math.Clamp(pageSize ?? 25, 1, 100), status), cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> CreateAsync(NewCatalogItem pipeline, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        await admin.CreatePipelineAsync(pipeline, await http.ActorAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        var detail = (await admin.GetPipelineAsync(pipeline.Key, cancellationToken).ConfigureAwait(false))!;
        ETags.Set(http, detail.RowVersion);
        return TypedResults.Created($"{http.Request.PathBase}{http.Request.Path}/{detail.Key}", detail);
    }

    /// <summary>Validates a pipeline that is not saved (the visual editor checks while editing).</summary>
    private static ValueTask<ValidationReport> ValidateAsync(PipelineDraftData data, IPipelineValidator validator, CancellationToken cancellationToken) =>
        validator.ValidateAsync(new PipelineDefinition
        {
            Key = "draft",
            Version = 1,
            Name = "draft",
            Inputs = data.Inputs,
            Steps = data.Steps,
            Outputs = data.Outputs,
        }, cancellationToken);

    private static async Task<IResult> GetAsync(string key, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        if (await admin.GetPipelineAsync(key, cancellationToken).ConfigureAwait(false) is not { } pipeline)
        {
            return ApiProblems.NotFound(http, $"Pipeline '{key}' was not found.");
        }

        ETags.Set(http, pipeline.RowVersion);
        return TypedResults.Ok(pipeline);
    }

    private static async Task<IResult> GetVersionAsync(string key, int version, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken) =>
        await admin.GetPipelineVersionAsync(key, version, cancellationToken).ConfigureAwait(false) is { } detail
            ? TypedResults.Ok(detail)
            : ApiProblems.NotFound(http, $"Version {version} of pipeline '{key}' was not found.");

    private static async Task<IResult> GetDraftAsync(string key, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        if (await admin.GetPipelineDraftAsync(key, cancellationToken).ConfigureAwait(false) is not { } draft)
        {
            return ApiProblems.NotFound(http, $"Pipeline '{key}' has no draft.");
        }

        ETags.Set(http, draft.RowVersion);
        return TypedResults.Ok(draft);
    }

    private static async Task<IResult> CreateDraftAsync(string key, NewDraftRequest request, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        var draft = await admin.CreatePipelineDraftFromAsync(key, request.FromVersion, await http.ActorAsync().ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        ETags.Set(http, draft.RowVersion);
        return TypedResults.Created($"{http.Request.PathBase}{http.Request.Path}", draft);
    }

    private static async Task<IResult> SaveDraftAsync(string key, PipelineDraftData data, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        if (ETags.IfMatch(http) is not { } rowVersion)
        {
            return ETags.Required(http);
        }

        if (await admin.GetPipelineDraftAsync(key, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return ApiProblems.NotFound(http, $"Pipeline '{key}' has no draft.");
        }

        var draft = await admin.SavePipelineDraftAsync(key, current.Version, data, rowVersion, await http.ActorAsync().ConfigureAwait(false), cancellationToken)
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

        if (await admin.GetPipelineDraftAsync(key, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return ApiProblems.NotFound(http, $"Pipeline '{key}' has no draft.");
        }

        await admin.DiscardPipelineDraftAsync(key, current.Version, rowVersion, await http.ActorAsync().ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> TestDraftAsync(
        string key, HttpContext http, ICatalogAdmin admin, IOptions<PythonScriptRunnerOptions> options, CancellationToken cancellationToken)
    {
        var inputs = await RunInputs.ReadAsync(http, options.Value, cancellationToken).ConfigureAwait(false);
        await using (inputs.ConfigureAwait(false))
        {
            var draft = await admin.GetPipelineDraftAsync(key, cancellationToken).ConfigureAwait(false);
            var handle = await admin.EnqueuePipelineDraftTestAsync(key, inputs.Values(draft?.Data.Inputs), await http.ActorAsync().ConfigureAwait(false),
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

        if (await admin.GetPipelineDraftAsync(key, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return ApiProblems.NotFound(http, $"Pipeline '{key}' has no draft.");
        }

        return TypedResults.Ok(await admin.PublishPipelineAsync(key, current.Version, rowVersion, await http.ActorAsync().ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false));
    }

    private static async Task<PipelineVersionDetail> ArchiveAsync(string key, int version, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken) =>
        await admin.ArchivePipelineVersionAsync(key, version, await http.ActorAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> DeleteAsync(string key, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        await admin.DeletePipelineAsync(key, await http.ActorAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> UpdateSettingsAsync(string key, PipelineSettings settings, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        if (ETags.IfMatch(http) is not { } rowVersion)
        {
            return ETags.Required(http);
        }

        var pipeline = await admin.UpdatePipelineSettingsAsync(key, settings, rowVersion, await http.ActorAsync().ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        ETags.Set(http, pipeline.RowVersion);
        return TypedResults.Ok(pipeline);
    }

    private static async Task<IResult> UpdateRetentionAsync(
        string key, PipelineRetention retention, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        if (ETags.IfMatch(http) is not { } rowVersion)
        {
            return ETags.Required(http);
        }

        var pipeline = await admin.UpdatePipelineRetentionAsync(key, retention, rowVersion, await http.ActorAsync().ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        ETags.Set(http, pipeline.RowVersion);
        return TypedResults.Ok(pipeline);
    }

    private static async Task<IResult> ExportAsync(string key, int version, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        if (await admin.GetPipelineVersionAsync(key, version, cancellationToken).ConfigureAwait(false) is null)
        {
            return ApiProblems.NotFound(http, $"Version {version} of pipeline '{key}' was not found.");
        }

        return await Endpoints.ZipAsync(zip => admin.ExportPipelineVersionAsync(key, version, zip, cancellationToken), $"{key}@{version}.zip")
            .ConfigureAwait(false);
    }

    private static async Task<IResult> RunAsync(
        string key, HttpContext http, IRunQueue queue, ICatalog catalog, IAuditWriter audit, IOptions<PythonScriptRunnerOptions> options,
        CancellationToken cancellationToken)
    {
        var inputs = await RunInputs.ReadAsync(http, options.Value, cancellationToken).ConfigureAwait(false);
        await using (inputs.ConfigureAwait(false))
        {
            var actor = await http.ActorAsync().ConfigureAwait(false);
            PipelineDefinition? pipeline;
            try
            {
                pipeline = await catalog.FindPipelineAsync(key, inputs.Version, cancellationToken).ConfigureAwait(false);
            }
            catch (VersionNotRunnableException)
            {
                pipeline = null;
            }

            var handle = await queue.EnqueuePipelineAsync(key, inputs.Values(pipeline?.Inputs), new RunOptions
            {
                Version = inputs.Version,
                IdempotencyKey = inputs.IdempotencyKey,
                TriggeredBy = actor.Subject,
                Trigger = RunTrigger.Dashboard,
            }, cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(new AuditEntry(actor, AuditActions.RunTriggered, "Run", handle.RunId.Value, key, new JsonObject
            {
                ["kind"] = nameof(RunKind.Pipeline),
                ["version"] = inputs.Version,
                ["deduplicated"] = handle.Deduplicated,
            }), cancellationToken).ConfigureAwait(false);
            return TypedResults.Accepted(http.RunLocation(handle.RunId), handle);
        }
    }

    private static async Task<IResult> CreateScheduleAsync(string key, ScheduleData schedule, HttpContext http, ICatalogAdmin admin, CancellationToken cancellationToken)
    {
        var created = await admin.CreateScheduleAsync(key, schedule, await http.ActorAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        ETags.Set(http, created.RowVersion);
        return TypedResults.Created($"{http.Request.PathBase}{http.RequestServices.GetRequiredService<Access.DashboardRoutes>().Prefix}/api/schedules/{created.Id}",
            created);
    }

    /// <summary>Imports a zip in the folder catalog format: the raw body, or the first file of a multipart request.</summary>
    private static async Task<IResult> ImportAsync(HttpContext http, ICatalogAdmin admin, IOptions<PythonScriptRunnerOptions> options, CancellationToken cancellationToken)
    {
        var limit = options.Value.MaxFileBytes;
        var buffer = new MemoryStream();
        await using (buffer.ConfigureAwait(false))
        {
            var source = http.Request.Body;
            if (http.Request.HasFormContentType && MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var mediaType)
                && HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value is { Length: > 0 } boundary)
            {
                var reader = new MultipartReader(boundary, http.Request.Body);
                MultipartSection? section;
                while ((section = await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false)) is not null
                    && !(ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) && disposition.IsFileDisposition()))
                {
                }

                source = section?.Body ?? throw new ArgumentException("The multipart request has no zip file.");
            }

            var chunk = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    throw new PayloadTooLargeException($"The archive exceeds {limit / (1024 * 1024)} MB.");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            buffer.Position = 0;
            return TypedResults.Ok(await admin.ImportAsync(buffer, await http.ActorAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false));
        }
    }
}
