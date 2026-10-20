using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PythonScriptRunner.EntityFrameworkCore.Entities;
using PythonScriptRunner.EntityFrameworkCore.Infrastructure;

namespace PythonScriptRunner.EntityFrameworkCore.Catalog;

internal sealed partial class EfCatalogAdmin
{
    public async Task<Page<PipelineSummary>> ListPipelinesAsync(CatalogQuery query, CancellationToken cancellationToken = default)
    {
        var (pageNumber, pageSize) = Paging(query);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var pipelines = db.Pipelines.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var pattern = $"%{query.Search.Trim()}%";
                pipelines = pipelines.Where(p => EF.Functions.ILike(p.Key, pattern) || EF.Functions.ILike(p.Name, pattern));
            }

            var total = await pipelines.CountAsync(cancellationToken).ConfigureAwait(false);
            var items = await pipelines.OrderBy(p => p.Key)
                .Skip((pageNumber - 1) * pageSize).Take(pageSize)
                .Select(p => new PipelineSummary(
                    p.Key, p.Name, p.Description, p.LatestPublishedVersion,
                    p.Versions.Where(v => v.Status == VersionStatus.Draft).Select(v => (int?)v.Version).FirstOrDefault(),
                    p.UpdatedAt))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return new Page<PipelineSummary>(items, pageNumber, pageSize, total);
        }
    }

    public async Task<PipelineDetail?> GetPipelineAsync(string key, CancellationToken cancellationToken = default)
    {
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var pipeline = await db.Pipelines.AsNoTracking().Include(p => p.Versions).SingleOrDefaultAsync(p => p.Key == key, cancellationToken).ConfigureAwait(false);
            return pipeline is null ? null : ToPipelineDetail(pipeline);
        }
    }

    public async Task<PipelineVersionDetail?> GetPipelineVersionAsync(string key, int version, CancellationToken cancellationToken = default)
    {
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await db.PipelineVersions.AsNoTracking().Include(v => v.Pipeline)
                .SingleOrDefaultAsync(v => v.Pipeline.Key == key && v.Version == version, cancellationToken).ConfigureAwait(false);
            return row is null
                ? null
                : CatalogMapping.ToPipelineVersionDetail(row.Pipeline, row, await EfCatalog.LoadStepsAsync(db, row.Id, cancellationToken).ConfigureAwait(false));
        }
    }

    public async Task<PipelineDraft?> GetPipelineDraftAsync(string key, CancellationToken cancellationToken = default)
    {
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await db.PipelineVersions.AsNoTracking().Include(v => v.Pipeline)
                .SingleOrDefaultAsync(v => v.Pipeline.Key == key && v.Status == VersionStatus.Draft, cancellationToken).ConfigureAwait(false);
            return row is null ? null : await ToPipelineDraftAsync(db, row, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<PipelineDraft> CreatePipelineAsync(NewCatalogItem pipeline, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(actor);
        ValidateItem(pipeline);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            if (await db.Pipelines.AnyAsync(p => p.Key == pipeline.Key, cancellationToken).ConfigureAwait(false))
            {
                throw new PsrDomainException(PsrErrorCodes.PipelineKeyTaken, $"Pipeline '{pipeline.Key}' already exists.");
            }

            var now = time.GetUtcNow();
            var row = new PipelineEntity
            {
                Id = Guid.CreateVersion7(now),
                Key = pipeline.Key,
                Name = pipeline.Name,
                Description = pipeline.Description,
                CreatedAt = now,
                CreatedBy = actor.Subject,
                UpdatedAt = now,
                UpdatedBy = actor.Subject,
            };
            var draft = NewPipelineVersion(row, 1, actor, now);
            row.Versions.Add(draft);
            db.Pipelines.Add(row);
            AuditLog.Add(db, new AuditEntry(actor, AuditActions.PipelineCreated, "Pipeline", row.Id, row.Key), now);
            await SaveAsync(db, cancellationToken, unique: _ => new PsrDomainException(PsrErrorCodes.PipelineKeyTaken, $"Pipeline '{pipeline.Key}' already exists."))
                .ConfigureAwait(false);
            return new PipelineDraft(row.Key, 1, new PipelineDraftData(), draft.RowVersion, CatalogMapping.Authorship(draft));
        }
    }

    public async Task<PipelineDetail> UpdatePipelineAsync(
        string key, CatalogItemChanges changes, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return await ChangePipelineAsync(key, expectedRowVersion, actor, AuditActions.PipelineUpdated, (pipeline, details) =>
        {
            if (changes.Name is { } name && name != pipeline.Name)
            {
                ValidateName(name);
                details["name"] = new JsonArray(pipeline.Name, name);
                pipeline.Name = name;
            }

            if (changes.Description is { } description && description != pipeline.Description)
            {
                details["description"] = new JsonArray(pipeline.Description, description);
                pipeline.Description = description;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PipelineDraft> CreatePipelineDraftFromAsync(string key, int fromVersion, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var pipeline = await db.Pipelines.Include(p => p.Versions).ThenInclude(v => v.Steps)
                .SingleOrDefaultAsync(p => p.Key == key, cancellationToken).ConfigureAwait(false)
                ?? throw new PipelineNotFoundException(key);
            EnsureNoDraft(pipeline.Versions.Any(v => v.Status == VersionStatus.Draft), key);
            var source = pipeline.Versions.SingleOrDefault(v => v.Version == fromVersion) ?? throw new VersionNotFoundException(key, fromVersion);
            var now = time.GetUtcNow();
            var draft = NewPipelineVersion(pipeline, pipeline.Versions.Max(v => v.Version) + 1, actor, now);
            draft.InputFields = source.InputFields;
            draft.Outputs = source.Outputs;
            draft.Layout = source.Layout;
            draft.Steps.AddRange(source.Steps.Select(s => new PipelineStepEntity
            {
                Id = Guid.CreateVersion7(now),
                PipelineVersionId = draft.Id,
                Order = s.Order,
                Key = s.Key,
                DisplayName = s.DisplayName,
                StageVersionId = s.StageVersionId,
                Bindings = s.Bindings,
            }));
            pipeline.Versions.Add(draft);
            AuditLog.Add(db, new AuditEntry(actor, AuditActions.PipelineDraftCreated, "PipelineVersion", draft.Id, $"{key}@{draft.Version}",
                new JsonObject { ["fromVersion"] = fromVersion }), now);
            await SaveAsync(db, cancellationToken, unique: _ => DraftExists(key)).ConfigureAwait(false);
            return await ToPipelineDraftAsync(db, draft, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<PipelineDraft> SavePipelineDraftAsync(
        string key, int version, PipelineDraftData data, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(actor);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await LoadPipelineVersionAsync(db, key, version, cancellationToken).ConfigureAwait(false);
            EnsureDraft(row.Status, key, version);
            if (row.RowVersion != expectedRowVersion)
            {
                throw new ConcurrencyConflictException($"The draft {key}@{version} was changed by someone else.",
                    await ToPipelineDraftAsync(db, row, cancellationToken).ConfigureAwait(false));
            }

            var steps = await BuildStepsAsync(db, row.Id, data.Steps, cancellationToken).ConfigureAwait(false);
            var now = time.GetUtcNow();
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var rollback = transaction.ConfigureAwait(false);
            await db.PipelineSteps.Where(s => s.PipelineVersionId == row.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            db.PipelineSteps.AddRange(steps);
            row.InputFields = StoredJson.Fields(data.Inputs);
            row.Outputs = StoredJson.Outputs(data.Outputs);
            row.Layout = StoredJson.Node(data.Layout);
            Touch(row, actor, now);
            db.Entry(row).Property(v => v.RowVersion).OriginalValue = expectedRowVersion;
            AuditLog.Add(db, new AuditEntry(actor, AuditActions.PipelineDraftSaved, "PipelineVersion", row.Id, $"{key}@{version}"), now);
            await SaveAsync(db, cancellationToken, conflict: Current(() => GetPipelineDraftAsync(key, CancellationToken.None))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await ToPipelineDraftAsync(db, row, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DiscardPipelineDraftAsync(string key, int version, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await LoadPipelineVersionAsync(db, key, version, cancellationToken).ConfigureAwait(false);
            EnsureDraft(row.Status, key, version);
            if (row.RowVersion != expectedRowVersion)
            {
                throw new ConcurrencyConflictException($"The draft {key}@{version} was changed by someone else.",
                    await ToPipelineDraftAsync(db, row, cancellationToken).ConfigureAwait(false));
            }

            var onlyVersion = !await db.PipelineVersions.AnyAsync(v => v.PipelineId == row.PipelineId && v.Id != row.Id, cancellationToken).ConfigureAwait(false);
            db.Entry(row).Property(v => v.RowVersion).OriginalValue = expectedRowVersion;
            db.PipelineVersions.Remove(row);
            if (onlyVersion && !await db.Runs.AnyAsync(r => r.PipelineId == row.PipelineId, cancellationToken).ConfigureAwait(false))
            {
                // A pipeline without versions cannot get a new draft; discarding its first draft removes it.
                db.Pipelines.Remove(row.Pipeline);
            }

            AuditLog.Add(db, new AuditEntry(actor, AuditActions.PipelineDraftDiscarded, "PipelineVersion", row.Id, $"{key}@{version}"), time.GetUtcNow());
            await SaveAsync(db, cancellationToken, conflict: Current(() => GetPipelineDraftAsync(key, CancellationToken.None))).ConfigureAwait(false);
            catalog.InvalidatePipeline(key);
        }
    }

    public async Task<PipelinePublication> PublishPipelineAsync(
        string key, int version, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await LoadPipelineVersionAsync(db, key, version, cancellationToken).ConfigureAwait(false);
            EnsureDraft(row.Status, key, version);
            if (row.RowVersion != expectedRowVersion)
            {
                throw new ConcurrencyConflictException($"The draft {key}@{version} was changed by someone else.",
                    await ToPipelineDraftAsync(db, row, cancellationToken).ConfigureAwait(false));
            }

            var steps = await EfCatalog.LoadStepsAsync(db, row.Id, cancellationToken).ConfigureAwait(false);
            var definition = CatalogMapping.ToPipeline(row.Pipeline, row, steps);
            var report = await pipelineValidator.ValidateAsync(definition, cancellationToken).ConfigureAwait(false);
            if (!report.IsValid)
            {
                throw new DefinitionInvalidException(PsrErrorCodes.PipelineInvalid, report);
            }

            List<ValidationIssue> warnings = [.. report.Warnings, .. await ScheduleWarningsAsync(db, row.PipelineId, definition, cancellationToken).ConfigureAwait(false)];
            var now = time.GetUtcNow();
            row.Status = VersionStatus.Published;
            row.PublishedAt = now;
            row.PublishedBy = actor.Subject;
            Touch(row, actor, now);
            row.Pipeline.LatestPublishedVersion = Math.Max(row.Pipeline.LatestPublishedVersion ?? 0, version);
            Touch(row.Pipeline, actor, now);
            db.Entry(row).Property(v => v.RowVersion).OriginalValue = expectedRowVersion;
            AuditLog.Add(db, new AuditEntry(actor, AuditActions.PipelinePublished, "PipelineVersion", row.Id, $"{key}@{version}"), now);
            await SaveAsync(db, cancellationToken, conflict: Current(() => GetPipelineDraftAsync(key, CancellationToken.None))).ConfigureAwait(false);
            catalog.InvalidatePipeline(key);
            return new PipelinePublication(CatalogMapping.ToPipelineVersionDetail(row.Pipeline, row, steps), warnings);
        }
    }

    public async Task<PipelineVersionDetail> ArchivePipelineVersionAsync(string key, int version, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await LoadPipelineVersionAsync(db, key, version, cancellationToken).ConfigureAwait(false);
            var steps = await EfCatalog.LoadStepsAsync(db, row.Id, cancellationToken).ConfigureAwait(false);
            if (row.Status == VersionStatus.Archived)
            {
                return CatalogMapping.ToPipelineVersionDetail(row.Pipeline, row, steps);
            }

            if (row.Status != VersionStatus.Published)
            {
                throw new PsrDomainException(PsrErrorCodes.VersionImmutable, $"Only published versions can be archived; discard the draft {key}@{version} instead.");
            }

            var schedules = await db.Schedules.AsNoTracking()
                .Where(s => s.PipelineId == row.PipelineId && s.IsEnabled && s.PinnedVersion == version)
                .Select(s => s.Name)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (schedules.Count > 0)
            {
                throw new PsrDomainException(PsrErrorCodes.VersionScheduled,
                    $"Version {key}@{version} is pinned by enabled schedules: {string.Join(", ", schedules)}.");
            }

            var now = time.GetUtcNow();
            row.Status = VersionStatus.Archived;
            row.ArchivedAt = now;
            row.ArchivedBy = actor.Subject;
            Touch(row, actor, now);
            if (row.Pipeline.LatestPublishedVersion == version)
            {
                row.Pipeline.LatestPublishedVersion = await db.PipelineVersions
                    .Where(v => v.PipelineId == row.PipelineId && v.Status == VersionStatus.Published && v.Id != row.Id)
                    .MaxAsync(v => (int?)v.Version, cancellationToken).ConfigureAwait(false);
                Touch(row.Pipeline, actor, now);
            }

            AuditLog.Add(db, new AuditEntry(actor, AuditActions.PipelineVersionArchived, "PipelineVersion", row.Id, $"{key}@{version}"), now);
            await SaveAsync(db, cancellationToken).ConfigureAwait(false);
            catalog.InvalidatePipeline(key);
            return CatalogMapping.ToPipelineVersionDetail(row.Pipeline, row, steps);
        }
    }

    public async Task DeletePipelineAsync(string key, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var pipeline = await db.Pipelines.SingleOrDefaultAsync(p => p.Key == key, cancellationToken).ConfigureAwait(false)
                ?? throw new PipelineNotFoundException(key);
            if (await db.Runs.AnyAsync(r => r.PipelineId == pipeline.Id, cancellationToken).ConfigureAwait(false))
            {
                throw new PsrDomainException(PsrErrorCodes.DeleteNotAllowed, $"Pipeline '{key}' has runs in the retained history.");
            }

            db.Pipelines.Remove(pipeline);
            AuditLog.Add(db, new AuditEntry(actor, AuditActions.PipelineDeleted, "Pipeline", pipeline.Id, key), time.GetUtcNow());
            await SaveAsync(db, cancellationToken).ConfigureAwait(false);
            catalog.InvalidatePipeline(key);
        }
    }

    public async Task<PipelineDetail> UpdatePipelineSettingsAsync(
        string key, PipelineSettings settings, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MaxConcurrentRuns is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "MaxConcurrentRuns must be at least 1 (or null for no limit).");
        }

        return await ChangePipelineAsync(key, expectedRowVersion, actor, AuditActions.PipelineSettingsChanged, (pipeline, details) =>
        {
            if (pipeline.MaxConcurrentRuns != settings.MaxConcurrentRuns)
            {
                details["maxConcurrentRuns"] = new JsonArray(pipeline.MaxConcurrentRuns, settings.MaxConcurrentRuns);
                pipeline.MaxConcurrentRuns = settings.MaxConcurrentRuns;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PipelineDetail> UpdatePipelineRetentionAsync(
        string key, PipelineRetention retention, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retention);
        CheckRetention(retention.ArtifactRetentionDays, options.Retention.Artifacts, "artifacts");
        CheckRetention(retention.RunRetentionDays, options.Retention.Runs, "runs");
        return await ChangePipelineAsync(key, expectedRowVersion, actor, AuditActions.PipelineSettingsChanged, (pipeline, details) =>
        {
            if (pipeline.ArtifactRetentionDays != retention.ArtifactRetentionDays)
            {
                details["artifactRetentionDays"] = new JsonArray(pipeline.ArtifactRetentionDays, retention.ArtifactRetentionDays);
                pipeline.ArtifactRetentionDays = retention.ArtifactRetentionDays;
            }

            if (pipeline.RunRetentionDays != retention.RunRetentionDays)
            {
                details["runRetentionDays"] = new JsonArray(pipeline.RunRetentionDays, retention.RunRetentionDays);
                pipeline.RunRetentionDays = retention.RunRetentionDays;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunResult> TestPipelineDraftAsync(string key, object? inputs, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        PipelineDefinition pipeline;
        Guid versionId;
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await db.PipelineVersions.AsNoTracking().Include(v => v.Pipeline)
                .SingleOrDefaultAsync(v => v.Pipeline.Key == key && v.Status == VersionStatus.Draft, cancellationToken).ConfigureAwait(false)
                ?? throw (await db.Pipelines.AnyAsync(p => p.Key == key, cancellationToken).ConfigureAwait(false)
                    ? new PsrDomainException(PsrErrorCodes.VersionImmutable, $"Pipeline '{key}' has no draft; create one from a version first.")
                    : new PipelineNotFoundException(key));
            pipeline = CatalogMapping.ToPipeline(row.Pipeline, row, await EfCatalog.LoadStepsAsync(db, row.Id, cancellationToken).ConfigureAwait(false));
            versionId = row.Id;
        }

        var report = await pipelineValidator.ValidateAsync(pipeline, cancellationToken).ConfigureAwait(false);
        if (!report.IsValid)
        {
            throw new DefinitionInvalidException(PsrErrorCodes.PipelineInvalid, report);
        }

        await WriteAuditAsync(new AuditEntry(actor, AuditActions.PipelineTested, "PipelineVersion", versionId, $"{key}@{pipeline.Version}"), cancellationToken)
            .ConfigureAwait(false);
        return await engine.RunPipelineAsync(pipeline, RunKind.PipelineTest, inputs, RunOptionsFor(actor), cancellationToken).ConfigureAwait(false);
    }

    private async Task<PipelineDetail> ChangePipelineAsync(
        string key, uint expectedRowVersion, Actor actor, string action, Action<PipelineEntity, JsonObject> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var pipeline = await db.Pipelines.Include(p => p.Versions).SingleOrDefaultAsync(p => p.Key == key, cancellationToken).ConfigureAwait(false)
                ?? throw new PipelineNotFoundException(key);
            if (pipeline.RowVersion != expectedRowVersion)
            {
                throw new ConcurrencyConflictException($"Pipeline '{key}' was changed by someone else.", ToPipelineDetail(pipeline));
            }

            var details = new JsonObject();
            change(pipeline, details);
            var now = time.GetUtcNow();
            Touch(pipeline, actor, now);
            db.Entry(pipeline).Property(p => p.RowVersion).OriginalValue = expectedRowVersion;
            AuditLog.Add(db, new AuditEntry(actor, action, "Pipeline", pipeline.Id, key, details), now);
            await SaveAsync(db, cancellationToken, conflict: Current(() => GetPipelineAsync(key, CancellationToken.None))).ConfigureAwait(false);
            catalog.InvalidatePipeline(key);
            return ToPipelineDetail(pipeline);
        }
    }

    /// <summary>Builds the step rows of a draft; every step must reference an existing stage version.</summary>
    private static async Task<List<PipelineStepEntity>> BuildStepsAsync(
        PsrDbContext db, Guid pipelineVersionId, IReadOnlyList<PipelineStepDefinition> steps, CancellationToken cancellationToken)
    {
        var issues = new List<ValidationIssue>();
        foreach (var duplicate in steps.GroupBy(s => s.Key, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            issues.Add(new ValidationIssue("PIPE_STEP_KEY", $"step key '{duplicate.Key}' is used more than once") { Step = duplicate.Key });
        }

        var keys = steps.Select(s => s.StageKey).Distinct().ToList();
        var versions = await db.StageVersions.AsNoTracking()
            .Where(v => keys.Contains(v.Stage.Key))
            .Select(v => new { v.Id, v.Stage.Key, v.Version })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<PipelineStepEntity>();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Key.Length is 0 or > 64 || !KeyPattern().IsMatch(step.Key))
            {
                issues.Add(new ValidationIssue("PIPE_STEP_KEY", $"step key '{step.Key}' must be a slug of up to 64 chars") { Step = step.Key });
            }

            var stageVersion = versions.FirstOrDefault(v => v.Key == step.StageKey && v.Version == step.StageVersion);
            if (stageVersion is null)
            {
                issues.Add(new ValidationIssue("PIPE_STAGE_NOT_PUBLISHED", $"stage '{step.StageKey}@{step.StageVersion}' does not exist") { Step = step.Key });
                continue;
            }

            result.Add(new PipelineStepEntity
            {
                Id = Guid.CreateVersion7(),
                PipelineVersionId = pipelineVersionId,
                Order = i + 1,
                Key = step.Key,
                DisplayName = step.DisplayName,
                StageVersionId = stageVersion.Id,
                Bindings = StoredJson.Bindings(step.Bindings),
            });
        }

        return issues.Count > 0 ? throw new DefinitionInvalidException(PsrErrorCodes.PipelineInvalid, new ValidationReport(issues, [])) : result;
    }

    private static async Task<PipelineVersionEntity> LoadPipelineVersionAsync(PsrDbContext db, string key, int version, CancellationToken cancellationToken)
    {
        var row = await db.PipelineVersions.Include(v => v.Pipeline)
            .SingleOrDefaultAsync(v => v.Pipeline.Key == key && v.Version == version, cancellationToken).ConfigureAwait(false);
        if (row is not null)
        {
            return row;
        }

        return await db.Pipelines.AnyAsync(p => p.Key == key, cancellationToken).ConfigureAwait(false)
            ? throw new VersionNotFoundException(key, version)
            : throw new PipelineNotFoundException(key);
    }

    private static async Task<PipelineDraft> ToPipelineDraftAsync(PsrDbContext db, PipelineVersionEntity row, CancellationToken cancellationToken)
    {
        var steps = await EfCatalog.LoadStepsAsync(db, row.Id, cancellationToken).ConfigureAwait(false);
        var definition = CatalogMapping.ToPipeline(row.Pipeline, row, steps);
        return new PipelineDraft(row.Pipeline.Key, row.Version, CatalogMapping.ToPipelineData(definition, row.Layout), row.RowVersion, CatalogMapping.Authorship(row));
    }

    private static PipelineDetail ToPipelineDetail(PipelineEntity pipeline) => new(
        pipeline.Key,
        pipeline.Name,
        pipeline.Description,
        pipeline.LatestPublishedVersion,
        [.. pipeline.Versions.OrderByDescending(v => v.Version).Select(v => new VersionSummary(v.Version, v.Status, v.PublishedAt, v.UpdatedAt))],
        new PipelineSettings(pipeline.MaxConcurrentRuns),
        new PipelineRetention(pipeline.ArtifactRetentionDays, pipeline.RunRetentionDays),
        pipeline.RowVersion,
        CatalogMapping.Authorship(pipeline));

    private static PipelineVersionEntity NewPipelineVersion(PipelineEntity pipeline, int version, Actor actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        PipelineId = pipeline.Id,
        Pipeline = pipeline,
        Version = version,
        Status = VersionStatus.Draft,
        InputFields = "[]",
        Outputs = "[]",
        CreatedAt = now,
        CreatedBy = actor.Subject,
        UpdatedAt = now,
        UpdatedBy = actor.Subject,
    };

    private static void CheckRetention(int? days, TimeSpan global, string what)
    {
        if (days is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(days), $"The {what} retention must be at least 1 day.");
        }

        if (days is { } value && TimeSpan.FromDays(value) > global)
        {
            throw new PsrDomainException(PsrErrorCodes.RetentionExceedsGlobal,
                $"The {what} retention ({value} days) exceeds the global retention ({global.TotalDays:0} days); pipelines can only shorten it.");
        }
    }

    private static Func<Task<object?>> Current<T>(Func<Task<T>> load) => async () => await load().ConfigureAwait(false);
}
