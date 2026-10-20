using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PythonScriptRunner.EntityFrameworkCore.Entities;
using PythonScriptRunner.EntityFrameworkCore.Infrastructure;

namespace PythonScriptRunner.EntityFrameworkCore.Catalog;

internal sealed partial class EfCatalogAdmin
{
    private const long MaxImportBytes = 50L * 1024 * 1024;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public async Task ExportStageVersionAsync(string key, int version, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await LoadStageVersionAsync(db, key, version, cancellationToken).ConfigureAwait(false);
            await WriteZipAsync(destination, zip => AddStage(zip, CatalogMapping.ToStage(row.Stage, row)), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ExportPipelineVersionAsync(string key, int version, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await LoadPipelineVersionAsync(db, key, version, cancellationToken).ConfigureAwait(false);
            var steps = await EfCatalog.LoadStepsAsync(db, row.Id, cancellationToken).ConfigureAwait(false);
            var pipeline = CatalogMapping.ToPipeline(row.Pipeline, row, steps);
            var stageIds = steps.Select(s => s.Step.StageVersionId).Distinct().ToList();
            var stages = await db.StageVersions.AsNoTracking().Include(v => v.Stage).Where(v => stageIds.Contains(v.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            await WriteZipAsync(destination, zip =>
            {
                AddText(zip, $"pipelines/{pipeline.Key}{CatalogManifests.PipelineSuffix}", CatalogManifests.WritePipeline(pipeline).ToJsonString(Indented));
                foreach (var stage in stages.OrderBy(s => s.Stage.Key).ThenBy(s => s.Version))
                {
                    AddStage(zip, CatalogMapping.ToStage(stage.Stage, stage));
                }
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ImportReport> ImportAsync(Stream zip, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentNullException.ThrowIfNull(actor);
        var buffer = new MemoryStream();
        await using (buffer.ConfigureAwait(false))
        {
            await zip.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (buffer.Length > MaxImportBytes)
            {
                throw Invalid([new ValidationIssue("IMPORT_TOO_LARGE", "the archive is larger than 50 MB")]);
            }

            buffer.Position = 0;
            var (stages, pipelines, issues) = ReadArchive(buffer);
            return await ImportAsync(stages, pipelines, issues, actor, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ImportReport> ImportAsync(
        List<StageDefinition> stages, List<PipelineDefinition> pipelines, List<ValidationIssue> issues, Actor actor, CancellationToken cancellationToken)
    {
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var now = time.GetUtcNow();
            var items = new List<ImportedItem>();
            var created = new Dictionary<(string Key, int Version), StageVersionEntity>();
            var stageKeys = stages.Select(s => s.Key).ToList();
            var existingStages = await db.Stages.Include(s => s.Versions).Where(s => stageKeys.Contains(s.Key))
                .ToDictionaryAsync(s => s.Key, cancellationToken).ConfigureAwait(false);
            foreach (var definition in stages)
            {
                var stage = existingStages.GetValueOrDefault(definition.Key);
                if (stage is not null && stage.Versions.Any(v => v.Status == VersionStatus.Draft))
                {
                    issues.Add(new ValidationIssue(PsrErrorCodes.DraftAlreadyExists, $"stage '{definition.Key}' already has a draft") { Field = definition.Key });
                    continue;
                }

                stage ??= NewImportedStage(db, definition, actor, now);
                var draft = NewStageVersion(stage, stage.Versions.Count == 0 ? 1 : stage.Versions.Max(v => v.Version) + 1, actor, now);
                CatalogMapping.Apply(draft, CatalogMapping.ToStageData(definition, runnerOptions.Value.DefaultPythonVersion));
                stage.Versions.Add(draft);
                created[(definition.Key, definition.Version)] = draft;
                items.Add(new ImportedItem("Stage", definition.Key, draft.Version, !existingStages.ContainsKey(definition.Key)));
            }

            var pipelineKeys = pipelines.Select(p => p.Key).ToList();
            var existingPipelines = await db.Pipelines.Include(p => p.Versions).Where(p => pipelineKeys.Contains(p.Key))
                .ToDictionaryAsync(p => p.Key, cancellationToken).ConfigureAwait(false);
            var references = pipelines.SelectMany(p => p.Steps).Select(s => s.StageKey).Distinct().ToList();
            var dbVersions = await db.StageVersions.AsNoTracking().Where(v => references.Contains(v.Stage.Key))
                .Select(v => new { v.Id, v.Stage.Key, v.Version })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var definition in pipelines)
            {
                var pipeline = existingPipelines.GetValueOrDefault(definition.Key);
                if (pipeline is not null && pipeline.Versions.Any(v => v.Status == VersionStatus.Draft))
                {
                    issues.Add(new ValidationIssue(PsrErrorCodes.DraftAlreadyExists, $"pipeline '{definition.Key}' already has a draft") { Field = definition.Key });
                    continue;
                }

                pipeline ??= NewImportedPipeline(db, definition, actor, now);
                var draft = NewPipelineVersion(pipeline, pipeline.Versions.Count == 0 ? 1 : pipeline.Versions.Max(v => v.Version) + 1, actor, now);
                draft.InputFields = StoredJson.Fields(definition.Inputs);
                draft.Outputs = StoredJson.Outputs(definition.Outputs);
                for (var i = 0; i < definition.Steps.Count; i++)
                {
                    var step = definition.Steps[i];
                    var entity = new PipelineStepEntity
                    {
                        Id = Guid.CreateVersion7(now),
                        PipelineVersionId = draft.Id,
                        Order = i + 1,
                        Key = step.Key,
                        DisplayName = step.DisplayName,
                        Bindings = StoredJson.Bindings(step.Bindings),
                    };

                    // A stage imported in the same archive may get another version number here: follow it.
                    if (created.TryGetValue((step.StageKey, step.StageVersion), out var imported))
                    {
                        entity.StageVersion = imported;
                    }
                    else if (dbVersions.FirstOrDefault(v => v.Key == step.StageKey && v.Version == step.StageVersion) is { } existing)
                    {
                        entity.StageVersionId = existing.Id;
                    }
                    else
                    {
                        issues.Add(new ValidationIssue("PIPE_STAGE_NOT_PUBLISHED", $"stage '{step.StageKey}@{step.StageVersion}' is not in the archive nor in the catalog")
                        {
                            Step = step.Key,
                            Field = definition.Key,
                        });
                    }

                    draft.Steps.Add(entity);
                }

                pipeline.Versions.Add(draft);
                items.Add(new ImportedItem("Pipeline", definition.Key, draft.Version, !existingPipelines.ContainsKey(definition.Key)));
            }

            if (issues.Count > 0)
            {
                throw Invalid(issues);
            }

            AuditLog.Add(db, new AuditEntry(actor, AuditActions.CatalogImported, "Settings", null, null, new JsonObject
            {
                ["items"] = new JsonArray([.. items.Select(i => (JsonNode)JsonValue.Create($"{i.Kind}:{i.Key}@{i.Version}"))]),
            }), now);
            await SaveAsync(db, cancellationToken, unique: _ => Invalid([new ValidationIssue(PsrErrorCodes.DraftAlreadyExists, "a draft was created concurrently")]))
                .ConfigureAwait(false);
            foreach (var item in items)
            {
                if (item.Kind == "Stage")
                {
                    catalog.InvalidateStage(item.Key);
                }
                else
                {
                    catalog.InvalidatePipeline(item.Key);
                }
            }

            return new ImportReport(items);
        }
    }

    private (List<StageDefinition> Stages, List<PipelineDefinition> Pipelines, List<ValidationIssue> Issues) ReadArchive(Stream buffer)
    {
        var stages = new List<StageDefinition>();
        var pipelines = new List<PipelineDefinition>();
        var issues = new List<ValidationIssue>();
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw Invalid([new ValidationIssue("IMPORT_NOT_ZIP", ex.Message)]);
        }

        using (archive)
        {
            var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name))
                .ToDictionary(e => e.FullName.Replace('\\', '/'), StringComparer.Ordinal);
            foreach (var (path, entry) in entries.Where(e => IsStageManifest(e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                var folder = path[..path.LastIndexOf('/')];
                try
                {
                    var manifest = Read(entry);
                    var json = JsonNode.Parse(manifest, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    var scriptPath = $"{folder}/{json?["script"]?.GetValue<string>() ?? CatalogManifests.ScriptName}";
                    var requirementsName = json?["requirements"]?.GetValue<string>();
                    var requirementsPath = $"{folder}/{requirementsName ?? CatalogManifests.RequirementsName}";
                    if (!entries.TryGetValue(scriptPath, out var script))
                    {
                        issues.Add(new ValidationIssue("IMPORT_SCRIPT_MISSING", $"script not found: {scriptPath}") { Field = path });
                        continue;
                    }

                    string? requirements = entries.TryGetValue(requirementsPath, out var requirementsEntry) ? Read(requirementsEntry) : null;
                    if (requirements is null && requirementsName is not null)
                    {
                        issues.Add(new ValidationIssue("IMPORT_REQUIREMENTS_MISSING", $"requirements not found: {requirementsPath}") { Field = path });
                        continue;
                    }

                    var stage = CatalogManifests.ReadStage(manifest, ScriptSource.Inline(Read(script)), requirements, null);
                    var report = stageValidator.Validate(stage);
                    issues.AddRange(report.Errors.Select(e => e with { Field = path }));
                    if (stages.Any(s => s.Key == stage.Key && s.Version == stage.Version))
                    {
                        issues.Add(new ValidationIssue("IMPORT_DUPLICATE", $"stage '{stage.Key}@{stage.Version}' appears twice") { Field = path });
                    }

                    stages.Add(stage);
                }
                catch (Exception ex) when (ex is PsrConfigurationException or JsonException or ArgumentException or InvalidDataException)
                {
                    issues.Add(new ValidationIssue("IMPORT_MANIFEST_INVALID", ex.Message) { Field = path });
                }
            }

            foreach (var (path, entry) in entries.Where(e => IsPipelineManifest(e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                try
                {
                    var pipeline = CatalogManifests.ReadPipeline(Read(entry));
                    if (pipelines.Any(p => p.Key == pipeline.Key))
                    {
                        issues.Add(new ValidationIssue("IMPORT_DUPLICATE", $"pipeline '{pipeline.Key}' appears twice") { Field = path });
                    }

                    pipelines.Add(pipeline);
                }
                catch (Exception ex) when (ex is PsrConfigurationException or JsonException or ArgumentException or InvalidDataException)
                {
                    issues.Add(new ValidationIssue("IMPORT_MANIFEST_INVALID", ex.Message) { Field = path });
                }
            }
        }

        if (stages.Count == 0 && pipelines.Count == 0 && issues.Count == 0)
        {
            issues.Add(new ValidationIssue("IMPORT_EMPTY", "the archive has no stages/*/stage.json nor pipelines/*.pipeline.json"));
        }

        if (stages.GroupBy(s => s.Key).FirstOrDefault(g => g.Count() > 1) is { } repeated)
        {
            issues.Add(new ValidationIssue("IMPORT_DUPLICATE", $"stage '{repeated.Key}' appears in more than one version; import one version per archive"));
        }

        return (stages, pipelines, issues);
    }

    private static StageEntity NewImportedStage(PsrDbContext db, StageDefinition definition, Actor actor, DateTimeOffset now)
    {
        var stage = new StageEntity
        {
            Id = Guid.CreateVersion7(now),
            Key = definition.Key,
            Name = definition.Name,
            Description = definition.Description,
            CreatedAt = now,
            CreatedBy = actor.Subject,
            UpdatedAt = now,
            UpdatedBy = actor.Subject,
        };
        db.Stages.Add(stage);
        return stage;
    }

    private static PipelineEntity NewImportedPipeline(PsrDbContext db, PipelineDefinition definition, Actor actor, DateTimeOffset now)
    {
        var pipeline = new PipelineEntity
        {
            Id = Guid.CreateVersion7(now),
            Key = definition.Key,
            Name = definition.Name,
            Description = definition.Description,
            CreatedAt = now,
            CreatedBy = actor.Subject,
            UpdatedAt = now,
            UpdatedBy = actor.Subject,
        };
        db.Pipelines.Add(pipeline);
        return pipeline;
    }

    private static bool IsStageManifest(string path)
    {
        var parts = path.Split('/');
        return parts.Length == 3 && parts[0] == "stages" && parts[2] == CatalogManifests.StageManifestName;
    }

    private static bool IsPipelineManifest(string path)
    {
        var parts = path.Split('/');
        return parts.Length == 2 && parts[0] == "pipelines" && parts[1].EndsWith(CatalogManifests.PipelineSuffix, StringComparison.Ordinal);
    }

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void AddStage(ZipArchive zip, StageDefinition stage)
    {
        var folder = $"stages/{stage.Key}@{stage.Version}";
        AddText(zip, $"{folder}/{CatalogManifests.StageManifestName}", CatalogManifests.WriteStage(stage).ToJsonString(Indented));
        AddText(zip, $"{folder}/{CatalogManifests.ScriptName}", ((InlineScriptSource)stage.Script).Content);
        if (!string.IsNullOrWhiteSpace(stage.Requirements))
        {
            AddText(zip, $"{folder}/{CatalogManifests.RequirementsName}", stage.Requirements);
        }

        if (!string.IsNullOrWhiteSpace(stage.LockedRequirements))
        {
            AddText(zip, $"{folder}/{CatalogManifests.LockName}", stage.LockedRequirements);
        }
    }

    private static void AddText(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    /// <summary>Builds the zip in memory (catalog files are small) and copies it asynchronously to the destination.</summary>
    private static async Task WriteZipAsync(Stream destination, Action<ZipArchive> write, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        await using (buffer.ConfigureAwait(false))
        {
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                write(zip);
            }

            buffer.Position = 0;
            await buffer.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }

    private static DefinitionInvalidException Invalid(IReadOnlyList<ValidationIssue> issues) =>
        new(PsrErrorCodes.ImportInvalid, new ValidationReport(issues, []));
}
