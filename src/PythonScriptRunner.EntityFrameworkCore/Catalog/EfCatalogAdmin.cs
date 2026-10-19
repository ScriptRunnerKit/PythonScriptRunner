using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PythonScriptRunner.EntityFrameworkCore.Entities;
using PythonScriptRunner.EntityFrameworkCore.Infrastructure;

namespace PythonScriptRunner.EntityFrameworkCore.Catalog;

/// <summary>Edits the catalog stored in PostgreSQL (drafts with optimistic concurrency, publication, archiving, deletion, import/export).</summary>
internal sealed partial class EfCatalogAdmin(
    IDbContextFactory<PsrDbContext> factory,
    EfCatalog catalog,
    IStageValidator stageValidator,
    IPipelineValidator pipelineValidator,
    IEnvironmentManager environments,
    IRunEngine engine,
    IOptions<PythonScriptRunnerOptions> runnerOptions,
    PsrEntityFrameworkOptions options,
    TimeProvider time) : ICatalogAdmin
{
    private const int MaxScriptBytes = 1024 * 1024;

    private const string StageTemplate = """
        from psrunner import stage


        @stage
        def run(inputs, ctx):
            return {}
        """;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KeyPattern();

    public async Task<Page<StageSummary>> ListStagesAsync(CatalogQuery query, CancellationToken cancellationToken = default)
    {
        var (pageNumber, pageSize) = Paging(query);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var stages = db.Stages.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var pattern = $"%{query.Search.Trim()}%";
                stages = stages.Where(s => EF.Functions.ILike(s.Key, pattern) || EF.Functions.ILike(s.Name, pattern));
            }

            var total = await stages.CountAsync(cancellationToken).ConfigureAwait(false);
            var items = await stages.OrderBy(s => s.Key)
                .Skip((pageNumber - 1) * pageSize).Take(pageSize)
                .Select(s => new StageSummary(
                    s.Key, s.Name, s.Description, s.LatestPublishedVersion,
                    s.Versions.Where(v => v.Status == VersionStatus.Draft).Select(v => (int?)v.Version).FirstOrDefault(),
                    s.UpdatedAt))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return new Page<StageSummary>(items, pageNumber, pageSize, total);
        }
    }

    public async Task<StageDetail?> GetStageAsync(string key, CancellationToken cancellationToken = default)
    {
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var stage = await db.Stages.AsNoTracking().Include(s => s.Versions).SingleOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false);
            return stage is null ? null : await ToStageDetailAsync(db, stage, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<StageVersionDetail?> GetStageVersionAsync(string key, int version, CancellationToken cancellationToken = default)
    {
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await db.StageVersions.AsNoTracking().Include(v => v.Stage)
                .SingleOrDefaultAsync(v => v.Stage.Key == key && v.Version == version, cancellationToken).ConfigureAwait(false);
            return row is null ? null : CatalogMapping.ToStageVersionDetail(row.Stage, row);
        }
    }

    public async Task<StageDraft?> GetStageDraftAsync(string key, CancellationToken cancellationToken = default)
    {
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await db.StageVersions.AsNoTracking().Include(v => v.Stage)
                .SingleOrDefaultAsync(v => v.Stage.Key == key && v.Status == VersionStatus.Draft, cancellationToken).ConfigureAwait(false);
            return row is null ? null : CatalogMapping.ToStageDraft(row.Stage, row);
        }
    }

    public async Task<StageDraft> CreateStageAsync(NewCatalogItem stage, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(actor);
        ValidateItem(stage);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            if (await db.Stages.AnyAsync(s => s.Key == stage.Key, cancellationToken).ConfigureAwait(false))
            {
                throw new PsrDomainException(PsrErrorCodes.StageKeyTaken, $"Stage '{stage.Key}' already exists.");
            }

            var now = time.GetUtcNow();
            var row = new StageEntity
            {
                Id = Guid.CreateVersion7(now),
                Key = stage.Key,
                Name = stage.Name,
                Description = stage.Description,
                CreatedAt = now,
                CreatedBy = actor.Subject,
                UpdatedAt = now,
                UpdatedBy = actor.Subject,
            };
            var draft = NewStageVersion(row, 1, actor, now);
            CatalogMapping.Apply(draft, new StageDraftData { Script = StageTemplate, PythonVersion = runnerOptions.Value.DefaultPythonVersion });
            row.Versions.Add(draft);
            db.Stages.Add(row);
            AuditLog.Add(db, new AuditEntry(actor, AuditActions.StageCreated, "Stage", row.Id, row.Key), now);
            await SaveAsync(db, cancellationToken, unique: _ => new PsrDomainException(PsrErrorCodes.StageKeyTaken, $"Stage '{stage.Key}' already exists."))
                .ConfigureAwait(false);
            return CatalogMapping.ToStageDraft(row, draft);
        }
    }

    public async Task<StageDetail> UpdateStageAsync(
        string key, CatalogItemChanges changes, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(actor);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var stage = await db.Stages.Include(s => s.Versions).SingleOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false)
                ?? throw new StageNotFoundException(key);
            if (stage.RowVersion != expectedRowVersion)
            {
                throw new ConcurrencyConflictException($"Stage '{key}' was changed by someone else.", await ToStageDetailAsync(db, stage, cancellationToken).ConfigureAwait(false));
            }

            var details = new JsonObject();
            if (changes.Name is { } name && name != stage.Name)
            {
                ValidateName(name);
                details["name"] = new JsonArray(stage.Name, name);
                stage.Name = name;
            }

            if (changes.Description is { } description && description != stage.Description)
            {
                details["description"] = new JsonArray(stage.Description, description);
                stage.Description = description;
            }

            var now = time.GetUtcNow();
            Touch(stage, actor, now);
            db.Entry(stage).Property(s => s.RowVersion).OriginalValue = expectedRowVersion;
            AuditLog.Add(db, new AuditEntry(actor, AuditActions.StageUpdated, "Stage", stage.Id, stage.Key, details), now);
            await SaveAsync(db, cancellationToken, conflict: Current(() => GetStageAsync(key, CancellationToken.None))).ConfigureAwait(false);
            catalog.InvalidateStage(key);
            return await ToStageDetailAsync(db, stage, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<StageDraft> CreateStageDraftFromAsync(string key, int fromVersion, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var stage = await db.Stages.Include(s => s.Versions).SingleOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false)
                ?? throw new StageNotFoundException(key);
            EnsureNoDraft(stage.Versions.Any(v => v.Status == VersionStatus.Draft), key);
            var source = stage.Versions.SingleOrDefault(v => v.Version == fromVersion) ?? throw new VersionNotFoundException(key, fromVersion);
            var now = time.GetUtcNow();
            var draft = NewStageVersion(stage, stage.Versions.Max(v => v.Version) + 1, actor, now);
            CatalogMapping.Apply(draft, CatalogMapping.ToStageData(source));
            stage.Versions.Add(draft);
            AuditLog.Add(db, new AuditEntry(actor, AuditActions.StageDraftCreated, "StageVersion", draft.Id, $"{key}@{draft.Version}",
                new JsonObject { ["fromVersion"] = fromVersion }), now);
            await SaveAsync(db, cancellationToken, unique: _ => DraftExists(key)).ConfigureAwait(false);
            return CatalogMapping.ToStageDraft(stage, draft);
        }
    }

    public async Task<StageDraft> SaveStageDraftAsync(
        string key, int version, StageDraftData data, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(actor);
        if (System.Text.Encoding.UTF8.GetByteCount(data.Script) > MaxScriptBytes)
        {
            throw new DefinitionInvalidException(PsrErrorCodes.StageInvalid, Report("STAGE_DEFINITION", "script is larger than 1 MB", field: "script"));
        }

        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await LoadStageVersionAsync(db, key, version, cancellationToken).ConfigureAwait(false);
            EnsureDraft(row.Status, key, version);
            if (row.RowVersion != expectedRowVersion)
            {
                throw new ConcurrencyConflictException($"The draft {key}@{version} was changed by someone else.", CatalogMapping.ToStageDraft(row.Stage, row));
            }

            var before = CatalogMapping.ToStageData(row);
            CatalogMapping.Apply(row, data);
            var now = time.GetUtcNow();
            Touch(row, actor, now);
            db.Entry(row).Property(v => v.RowVersion).OriginalValue = expectedRowVersion;
            AuditLog.Add(db, new AuditEntry(actor, AuditActions.StageDraftSaved, "StageVersion", row.Id, $"{key}@{version}",
                new JsonObject { ["changed"] = ChangedFields(before, data) }), now);
            await SaveAsync(db, cancellationToken, conflict: Current(() => GetStageDraftAsync(key, CancellationToken.None))).ConfigureAwait(false);
            return CatalogMapping.ToStageDraft(row.Stage, row);
        }
    }

    public async Task DiscardStageDraftAsync(string key, int version, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await LoadStageVersionAsync(db, key, version, cancellationToken).ConfigureAwait(false);
            EnsureDraft(row.Status, key, version);
            if (row.RowVersion != expectedRowVersion)
            {
                throw new ConcurrencyConflictException($"The draft {key}@{version} was changed by someone else.", CatalogMapping.ToStageDraft(row.Stage, row));
            }

            if (await db.PipelineSteps.AnyAsync(s => s.StageVersionId == row.Id, cancellationToken).ConfigureAwait(false))
            {
                throw new PsrDomainException(PsrErrorCodes.DeleteNotAllowed, $"The draft {key}@{version} is used by a pipeline draft.");
            }

            var now = time.GetUtcNow();
            var onlyVersion = !await db.StageVersions.AnyAsync(v => v.StageId == row.StageId && v.Id != row.Id, cancellationToken).ConfigureAwait(false);
            db.Entry(row).Property(v => v.RowVersion).OriginalValue = expectedRowVersion;
            db.StageVersions.Remove(row);
            if (onlyVersion)
            {
                // A stage without versions cannot get a new draft; discarding its first draft removes it.
                db.Stages.Remove(row.Stage);
            }

            AuditLog.Add(db, new AuditEntry(actor, AuditActions.StageDraftDiscarded, "StageVersion", row.Id, $"{key}@{version}"), now);
            await SaveAsync(db, cancellationToken, conflict: Current(() => GetStageDraftAsync(key, CancellationToken.None))).ConfigureAwait(false);
            catalog.InvalidateStage(key);
        }
    }

    public async Task<ValidationReport> ValidateStageDraftAsync(string key, CancellationToken cancellationToken = default)
    {
        var (stage, _) = await LoadStageDraftDefinitionAsync(key, cancellationToken).ConfigureAwait(false);
        return stageValidator.Validate(stage);
    }

    public async Task<StageVersionDetail> PublishStageAsync(
        string key, int version, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await LoadStageVersionAsync(db, key, version, cancellationToken).ConfigureAwait(false);
            EnsureDraft(row.Status, key, version);
            if (row.RowVersion != expectedRowVersion)
            {
                throw new ConcurrencyConflictException($"The draft {key}@{version} was changed by someone else.", CatalogMapping.ToStageDraft(row.Stage, row));
            }

            var definition = CatalogMapping.ToStage(row.Stage, row);
            var report = stageValidator.Validate(definition);
            if (!report.IsValid)
            {
                throw new DefinitionInvalidException(PsrErrorCodes.StageInvalid, report);
            }

            string locked;
            try
            {
                if (await environments.CheckSyntaxAsync(row.PythonVersion, row.Script, cancellationToken).ConfigureAwait(false) is { } syntax)
                {
                    throw new ScriptSyntaxException(syntax.Message, syntax.Line, syntax.Column);
                }

                locked = await environments.LockAsync(row.PythonVersion, row.Requirements, cancellationToken).ConfigureAwait(false);
            }
            catch (PythonEnvironmentException ex)
            {
                throw new PsrDomainException(PsrErrorCodes.DependencyResolutionFailed, ex.Message, ex);
            }

            var now = time.GetUtcNow();
            row.LockedRequirements = locked;
            row.EnvironmentHash = environments.ComputeHash(row.PythonVersion, locked);
            row.Status = VersionStatus.Published;
            row.PublishedAt = now;
            row.PublishedBy = actor.Subject;
            Touch(row, actor, now);
            row.Stage.LatestPublishedVersion = Math.Max(row.Stage.LatestPublishedVersion ?? 0, version);
            Touch(row.Stage, actor, now);
            db.Entry(row).Property(v => v.RowVersion).OriginalValue = expectedRowVersion;
            AuditLog.Add(db, new AuditEntry(actor, AuditActions.StagePublished, "StageVersion", row.Id, $"{key}@{version}",
                new JsonObject { ["environmentHash"] = row.EnvironmentHash }), now);
            await SaveAsync(db, cancellationToken, conflict: Current(() => GetStageDraftAsync(key, CancellationToken.None))).ConfigureAwait(false);
            catalog.InvalidateStage(key);
            return CatalogMapping.ToStageVersionDetail(row.Stage, row);
        }
    }

    public async Task<StageVersionDetail> ArchiveStageVersionAsync(string key, int version, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await LoadStageVersionAsync(db, key, version, cancellationToken).ConfigureAwait(false);
            if (row.Status == VersionStatus.Archived)
            {
                return CatalogMapping.ToStageVersionDetail(row.Stage, row);
            }

            if (row.Status != VersionStatus.Published)
            {
                throw new PsrDomainException(PsrErrorCodes.VersionImmutable, $"Only published versions can be archived; discard the draft {key}@{version} instead.");
            }

            var users = await db.PipelineSteps.AsNoTracking()
                .Where(s => s.StageVersionId == row.Id)
                .Join(db.PipelineVersions.Where(p => p.Status == VersionStatus.Published), s => s.PipelineVersionId, p => p.Id,
                    (s, p) => new { p.Pipeline.Key, p.Version })
                .Distinct()
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (users.Count > 0)
            {
                throw new VersionReferencedException(key, version, [.. users.OrderBy(u => u.Key).ThenBy(u => u.Version).Select(u => $"{u.Key}@{u.Version}")]);
            }

            var now = time.GetUtcNow();
            row.Status = VersionStatus.Archived;
            row.ArchivedAt = now;
            row.ArchivedBy = actor.Subject;
            Touch(row, actor, now);
            if (row.Stage.LatestPublishedVersion == version)
            {
                row.Stage.LatestPublishedVersion = await db.StageVersions
                    .Where(v => v.StageId == row.StageId && v.Status == VersionStatus.Published && v.Id != row.Id)
                    .MaxAsync(v => (int?)v.Version, cancellationToken).ConfigureAwait(false);
                Touch(row.Stage, actor, now);
            }

            AuditLog.Add(db, new AuditEntry(actor, AuditActions.StageVersionArchived, "StageVersion", row.Id, $"{key}@{version}"), now);
            await SaveAsync(db, cancellationToken).ConfigureAwait(false);
            catalog.InvalidateStage(key);
            return CatalogMapping.ToStageVersionDetail(row.Stage, row);
        }
    }

    public async Task DeleteStageAsync(string key, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var stage = await db.Stages.SingleOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false)
                ?? throw new StageNotFoundException(key);
            var users = await db.PipelineSteps.AsNoTracking()
                .Where(s => s.StageVersion.StageId == stage.Id)
                .Join(db.PipelineVersions, s => s.PipelineVersionId, p => p.Id, (s, p) => new { p.Pipeline.Key, p.Version })
                .Distinct()
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (users.Count > 0)
            {
                throw new PsrDomainException(PsrErrorCodes.DeleteNotAllowed,
                    $"Stage '{key}' is used by pipelines: {string.Join(", ", users.Select(u => $"{u.Key}@{u.Version}"))}.");
            }

            var hasRuns = await db.Runs.AnyAsync(r => r.StageKey == key, cancellationToken).ConfigureAwait(false)
                || await db.StepRuns.AnyAsync(s => s.StageKey == key, cancellationToken).ConfigureAwait(false);
            if (hasRuns)
            {
                throw new PsrDomainException(PsrErrorCodes.DeleteNotAllowed, $"Stage '{key}' has runs in the retained history.");
            }

            db.Stages.Remove(stage);
            AuditLog.Add(db, new AuditEntry(actor, AuditActions.StageDeleted, "Stage", stage.Id, key), time.GetUtcNow());
            await SaveAsync(db, cancellationToken).ConfigureAwait(false);
            catalog.InvalidateStage(key);
        }
    }

    public async Task<RunResult> TestStageDraftAsync(string key, object? inputs, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var (stage, versionId) = await LoadStageDraftDefinitionAsync(key, cancellationToken).ConfigureAwait(false);
        var report = stageValidator.Validate(stage);
        if (!report.IsValid)
        {
            throw new DefinitionInvalidException(PsrErrorCodes.StageInvalid, report);
        }

        await WriteAuditAsync(new AuditEntry(actor, AuditActions.StageTested, "StageVersion", versionId, $"{key}@{stage.Version}"), cancellationToken)
            .ConfigureAwait(false);
        return await engine.RunStageAsync(stage, RunKind.StageTest, inputs, RunOptionsFor(actor), cancellationToken).ConfigureAwait(false);
    }

    private async Task<(StageDefinition Stage, Guid VersionId)> LoadStageDraftDefinitionAsync(string key, CancellationToken cancellationToken)
    {
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await db.StageVersions.AsNoTracking().Include(v => v.Stage)
                .SingleOrDefaultAsync(v => v.Stage.Key == key && v.Status == VersionStatus.Draft, cancellationToken).ConfigureAwait(false)
                ?? throw (await db.Stages.AnyAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false)
                    ? new PsrDomainException(PsrErrorCodes.VersionImmutable, $"Stage '{key}' has no draft; create one from a version first.")
                    : new StageNotFoundException(key));
            return (CatalogMapping.ToStage(row.Stage, row), row.Id);
        }
    }

    private static async Task<StageVersionEntity> LoadStageVersionAsync(PsrDbContext db, string key, int version, CancellationToken cancellationToken)
    {
        var row = await db.StageVersions.Include(v => v.Stage)
            .SingleOrDefaultAsync(v => v.Stage.Key == key && v.Version == version, cancellationToken).ConfigureAwait(false);
        if (row is not null)
        {
            return row;
        }

        return await db.Stages.AnyAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false)
            ? throw new VersionNotFoundException(key, version)
            : throw new StageNotFoundException(key);
    }

    private static async Task<StageDetail> ToStageDetailAsync(PsrDbContext db, StageEntity stage, CancellationToken cancellationToken)
    {
        var versions = stage.Versions.Count > 0
            ? stage.Versions
            : await db.StageVersions.AsNoTracking().Where(v => v.StageId == stage.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var usages = await db.PipelineSteps.AsNoTracking()
            .Where(s => s.StageVersion.StageId == stage.Id)
            .Join(db.PipelineVersions, s => s.PipelineVersionId, p => p.Id,
                (s, p) => new StageUsage(p.Pipeline.Key, p.Version, p.Status, s.Key, s.StageVersion.Version))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new StageDetail(
            stage.Key,
            stage.Name,
            stage.Description,
            stage.LatestPublishedVersion,
            [.. versions.OrderByDescending(v => v.Version).Select(v => new VersionSummary(v.Version, v.Status, v.PublishedAt, v.UpdatedAt))],
            [.. usages.OrderBy(u => u.PipelineKey).ThenBy(u => u.PipelineVersion)],
            stage.RowVersion,
            CatalogMapping.Authorship(stage));
    }

    private static StageVersionEntity NewStageVersion(StageEntity stage, int version, Actor actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        StageId = stage.Id,
        Stage = stage,
        Version = version,
        Status = VersionStatus.Draft,
        Script = string.Empty,
        ScriptSha256 = string.Empty,
        PythonVersion = string.Empty,
        InputFields = "[]",
        OutputFields = "[]",
        CreatedAt = now,
        CreatedBy = actor.Subject,
        UpdatedAt = now,
        UpdatedBy = actor.Subject,
    };

    private static JsonArray ChangedFields(StageDraftData before, StageDraftData after)
    {
        var changed = new JsonArray();
        void Check(string name, bool same)
        {
            if (!same)
            {
                changed.Add(name);
            }
        }

        Check("script", before.Script == after.Script);
        Check("pythonVersion", before.PythonVersion == after.PythonVersion);
        Check("requirements", (before.Requirements ?? string.Empty) == (after.Requirements ?? string.Empty));
        Check("inputs", StoredJson.Fields(before.Inputs) == StoredJson.Fields(after.Inputs));
        Check("outputs", StoredJson.Fields(before.Outputs) == StoredJson.Fields(after.Outputs));
        Check("secrets", before.RequiredSecrets.SequenceEqual(after.RequiredSecrets));
        Check("limits", before.Timeout == after.Timeout && before.MemoryLimitMb == after.MemoryLimitMb);
        Check("retry", before.Retry == after.Retry && before.Idempotent == after.Idempotent);
        return changed;
    }

    private static void Touch(StageEntity row, Actor actor, DateTimeOffset now)
    {
        row.UpdatedAt = now;
        row.UpdatedBy = actor.Subject;
    }

    private static void Touch(StageVersionEntity row, Actor actor, DateTimeOffset now)
    {
        row.UpdatedAt = now;
        row.UpdatedBy = actor.Subject;
    }

    private static void Touch(PipelineEntity row, Actor actor, DateTimeOffset now)
    {
        row.UpdatedAt = now;
        row.UpdatedBy = actor.Subject;
    }

    private static void Touch(PipelineVersionEntity row, Actor actor, DateTimeOffset now)
    {
        row.UpdatedAt = now;
        row.UpdatedBy = actor.Subject;
    }

    private static void EnsureDraft(VersionStatus status, string key, int version)
    {
        if (status != VersionStatus.Draft)
        {
            throw new PsrDomainException(PsrErrorCodes.VersionImmutable,
                $"Version {key}@{version} is {status} and cannot change; create a draft from it instead.");
        }
    }

    private static void EnsureNoDraft(bool exists, string key)
    {
        if (exists)
        {
            throw DraftExists(key);
        }
    }

    private static PsrDomainException DraftExists(string key) =>
        new(PsrErrorCodes.DraftAlreadyExists, $"'{key}' already has a draft; edit or discard it first.");

    private static void ValidateItem(NewCatalogItem item)
    {
        if (item.Key.Length is < 3 or > 64 || !KeyPattern().IsMatch(item.Key))
        {
            throw new ArgumentException($"Key '{item.Key}' must be a slug of 3-64 chars (a-z, 0-9, '-').", nameof(item));
        }

        ValidateName(item.Name);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            throw new ArgumentException("Name must have 1-200 chars.", nameof(name));
        }
    }

    private static (int PageNumber, int PageSize) Paging(CatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return (Math.Max(1, query.PageNumber), Math.Clamp(query.PageSize, 1, 100));
    }

    private static ValidationReport Report(string code, string message, string? step = null, string? field = null) =>
        new([new ValidationIssue(code, message) { Step = step, Field = field }], []);

    private static RunOptions RunOptionsFor(Actor actor) =>
        new() { TriggeredBy = actor.Subject, Trigger = actor.IsHuman ? RunTrigger.Dashboard : RunTrigger.Library };

    private async Task WriteAuditAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        if (!entry.Actor.IsHuman)
        {
            return;
        }

        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            AuditLog.Add(db, entry, time.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Saves and maps unique violations and concurrency conflicts to domain errors.</summary>
    private static async Task SaveAsync(
        PsrDbContext db,
        CancellationToken cancellationToken,
        Func<Task<object?>>? conflict = null,
        Func<PostgresException, Exception>? unique = null)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var current = conflict is null ? null : await conflict().ConfigureAwait(false);
            throw new ConcurrencyConflictException($"The row was changed by someone else ({ex.Entries.Count} entries).", current);
        }
        catch (DbUpdateException ex) when (unique is not null && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres)
        {
            throw unique(postgres);
        }
    }
}
