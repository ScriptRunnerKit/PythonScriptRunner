using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PythonScriptRunner.EntityFrameworkCore.Entities;
using PythonScriptRunner.EntityFrameworkCore.Infrastructure;
using PythonScriptRunner.EntityFrameworkCore.Scheduling;

namespace PythonScriptRunner.EntityFrameworkCore.Catalog;

/// <summary>Cron schedules of pipelines (D-41, D-45, D-77, D-89).</summary>
internal sealed partial class EfCatalogAdmin
{
    public async Task<Page<ScheduleDetail>> ListSchedulesAsync(ScheduleQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (pageNumber, pageSize) = (Math.Max(1, query.PageNumber), Math.Clamp(query.PageSize, 1, 100));
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var rows = db.Schedules.AsNoTracking().Join(db.Pipelines, s => s.PipelineId, p => p.Id, (s, p) => new { Schedule = s, p.Key });
            if (query.PipelineKey is { } pipelineKey)
            {
                rows = rows.Where(r => r.Key == pipelineKey);
            }

            var total = await rows.CountAsync(cancellationToken).ConfigureAwait(false);
            var items = await rows.OrderBy(r => r.Schedule.Name).ThenBy(r => r.Schedule.Id)
                .Skip((pageNumber - 1) * pageSize).Take(pageSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return new Page<ScheduleDetail>([.. items.Select(r => ToScheduleDetail(r.Schedule, r.Key))], pageNumber, pageSize, total);
        }
    }

    public async Task<ScheduleDetail?> GetScheduleAsync(ScheduleId id, CancellationToken cancellationToken = default)
    {
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await db.Schedules.AsNoTracking().Join(db.Pipelines, s => s.PipelineId, p => p.Id, (s, p) => new { Schedule = s, p.Key })
                .SingleOrDefaultAsync(r => r.Schedule.Id == id.Value, cancellationToken).ConfigureAwait(false);
            return row is null ? null : ToScheduleDetail(row.Schedule, row.Key);
        }
    }

    public async Task<ScheduleDetail> CreateScheduleAsync(string pipelineKey, ScheduleData schedule, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(actor);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var pipeline = await db.Pipelines.AsNoTracking().SingleOrDefaultAsync(p => p.Key == pipelineKey, cancellationToken).ConfigureAwait(false)
                ?? throw new PipelineNotFoundException(pipelineKey);
            var cron = await ValidateScheduleAsync(pipelineKey, schedule, cancellationToken).ConfigureAwait(false);
            var now = time.GetUtcNow();
            var row = new ScheduleEntity
            {
                Id = Guid.CreateVersion7(now),
                PipelineId = pipeline.Id,
                Name = schedule.Name,
                Cron = schedule.Cron.Trim(),
                TimeZone = schedule.TimeZone,
                PinnedVersion = schedule.PinnedVersion,
                Inputs = schedule.Inputs.ToJsonString(),
                IsEnabled = schedule.IsEnabled,
                NextRunAt = schedule.IsEnabled ? cron.Next(now) : null,
                CreatedAt = now,
                CreatedBy = actor.Subject,
                UpdatedAt = now,
                UpdatedBy = actor.Subject,
            };
            db.Schedules.Add(row);
            AuditLog.Add(db, new AuditEntry(actor, AuditActions.ScheduleCreated, "Schedule", row.Id, ScheduleKey(pipelineKey, row.Name), Describe(row)), now);
            await SaveAsync(db, cancellationToken).ConfigureAwait(false);
            return ToScheduleDetail(row, pipelineKey);
        }
    }

    public async Task<ScheduleDetail> UpdateScheduleAsync(
        ScheduleId id, ScheduleData schedule, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(actor);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var (row, pipelineKey) = await LoadScheduleAsync(db, id, expectedRowVersion, cancellationToken).ConfigureAwait(false);
            var cron = await ValidateScheduleAsync(pipelineKey, schedule, cancellationToken).ConfigureAwait(false);
            var before = Describe(row);
            var now = time.GetUtcNow();
            row.Name = schedule.Name;
            row.Cron = schedule.Cron.Trim();
            row.TimeZone = schedule.TimeZone;
            row.PinnedVersion = schedule.PinnedVersion;
            row.Inputs = schedule.Inputs.ToJsonString();
            row.IsEnabled = schedule.IsEnabled;
            row.NextRunAt = schedule.IsEnabled ? cron.Next(now) : null;
            row.UpdatedAt = now;
            row.UpdatedBy = actor.Subject;
            var after = Describe(row);
            var changes = new JsonObject();
            foreach (var (name, value) in after)
            {
                if (!JsonNode.DeepEquals(before[name], value))
                {
                    changes[name] = new JsonArray(before[name]?.DeepClone(), value?.DeepClone());
                }
            }

            AuditLog.Add(db, new AuditEntry(actor, AuditActions.ScheduleUpdated, "Schedule", row.Id, ScheduleKey(pipelineKey, row.Name), changes), now);
            await SaveAsync(db, cancellationToken, conflict: Current(() => GetScheduleAsync(id, CancellationToken.None))).ConfigureAwait(false);
            return ToScheduleDetail(row, pipelineKey);
        }
    }

    public async Task<ScheduleDetail> SetScheduleEnabledAsync(
        ScheduleId id, bool enabled, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var (row, pipelineKey) = await LoadScheduleAsync(db, id, expectedRowVersion, cancellationToken).ConfigureAwait(false);
            if (row.IsEnabled == enabled)
            {
                return ToScheduleDetail(row, pipelineKey);
            }

            var now = time.GetUtcNow();
            row.IsEnabled = enabled;
            row.NextRunAt = enabled ? CronSchedule.Parse(row.Cron, row.TimeZone).Next(now) : null;
            row.UpdatedAt = now;
            row.UpdatedBy = actor.Subject;
            AuditLog.Add(db, new AuditEntry(actor, enabled ? AuditActions.ScheduleEnabled : AuditActions.ScheduleDisabled, "Schedule", row.Id,
                ScheduleKey(pipelineKey, row.Name)), now);
            await SaveAsync(db, cancellationToken, conflict: Current(() => GetScheduleAsync(id, CancellationToken.None))).ConfigureAwait(false);
            return ToScheduleDetail(row, pipelineKey);
        }
    }

    public async Task DeleteScheduleAsync(ScheduleId id, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await db.Schedules.Join(db.Pipelines, s => s.PipelineId, p => p.Id, (s, p) => new { Schedule = s, p.Key })
                .SingleOrDefaultAsync(r => r.Schedule.Id == id.Value, cancellationToken).ConfigureAwait(false)
                ?? throw ScheduleNotFound(id);
            db.Schedules.Remove(row.Schedule);
            AuditLog.Add(db, new AuditEntry(actor, AuditActions.ScheduleDeleted, "Schedule", row.Schedule.Id, ScheduleKey(row.Key, row.Schedule.Name),
                Describe(row.Schedule)), time.GetUtcNow());
            await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Warnings of D-77: enabled schedules that follow the latest version and do not provide a required input of the new one
    /// (their next firings will be rejected).
    /// </summary>
    private static async Task<List<ValidationIssue>> ScheduleWarningsAsync(
        PsrDbContext db, Guid pipelineId, PipelineDefinition published, CancellationToken cancellationToken)
    {
        var schedules = await db.Schedules.AsNoTracking()
            .Where(s => s.PipelineId == pipelineId && s.IsEnabled && s.PinnedVersion == null)
            .Select(s => new { s.Name, s.Inputs })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var warnings = new List<ValidationIssue>();
        foreach (var schedule in schedules)
        {
            var inputs = StoredJson.Object(schedule.Inputs) ?? [];
            var missing = ScheduleRules.MissingInputs(published, inputs)
                .Concat(published.Inputs.Where(f => f.Type.IsFile && f.Required).Select(f => f.Name))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (missing.Count > 0)
            {
                warnings.Add(new ValidationIssue("SCHEDULE_MISSING_INPUT",
                    $"schedule '{schedule.Name}' does not provide the required input(s) {string.Join(", ", missing)}; its firings will be rejected"));
            }
        }

        return warnings;
    }

    /// <summary>Validates name, cron, time zone, version and inputs; returns the parsed expression.</summary>
    private async Task<CronSchedule> ValidateScheduleAsync(string pipelineKey, ScheduleData schedule, CancellationToken cancellationToken)
    {
        ValidateName(schedule.Name);
        ArgumentNullException.ThrowIfNull(schedule.Inputs);
        var cron = CronSchedule.Parse(schedule.Cron, schedule.TimeZone);
        if (schedule.PinnedVersion is < 1)
        {
            throw new PsrDomainException(PsrErrorCodes.ScheduleInvalid, "The pinned version must be at least 1 (or null for the latest).");
        }

        var pipeline = await ScheduleRules.ResolveAsync(catalog, pipelineKey, schedule.PinnedVersion, cancellationToken).ConfigureAwait(false);
        ScheduleRules.EnsureNoFileInputs(pipeline, schedule.Inputs);
        engine.BindInputs(pipeline.Inputs, schedule.Inputs);
        return cron;
    }

    private static async Task<(ScheduleEntity Row, string PipelineKey)> LoadScheduleAsync(
        PsrDbContext db, ScheduleId id, uint expectedRowVersion, CancellationToken cancellationToken)
    {
        var loaded = await db.Schedules.Join(db.Pipelines, s => s.PipelineId, p => p.Id, (s, p) => new { Schedule = s, p.Key })
            .SingleOrDefaultAsync(r => r.Schedule.Id == id.Value, cancellationToken).ConfigureAwait(false)
            ?? throw ScheduleNotFound(id);
        if (loaded.Schedule.RowVersion != expectedRowVersion)
        {
            throw new ConcurrencyConflictException($"Schedule {id} was changed by someone else.", ToScheduleDetail(loaded.Schedule, loaded.Key));
        }

        db.Entry(loaded.Schedule).Property(s => s.RowVersion).OriginalValue = expectedRowVersion;
        return (loaded.Schedule, loaded.Key);
    }

    private static PsrDomainException ScheduleNotFound(ScheduleId id) => new(PsrErrorCodes.ScheduleNotFound, $"Schedule {id} was not found.");

    private static string ScheduleKey(string pipelineKey, string name)
    {
        var key = $"{pipelineKey}/{name}";
        return key.Length <= 100 ? key : key[..100];
    }

    private static JsonObject Describe(ScheduleEntity row) => new()
    {
        ["name"] = row.Name,
        ["cron"] = row.Cron,
        ["timeZone"] = row.TimeZone,
        ["pinnedVersion"] = row.PinnedVersion,
        ["inputs"] = StoredJson.Node(row.Inputs),
        ["isEnabled"] = row.IsEnabled,
    };

    private static ScheduleDetail ToScheduleDetail(ScheduleEntity row, string pipelineKey) => new()
    {
        Id = new ScheduleId(row.Id),
        PipelineKey = pipelineKey,
        Data = new ScheduleData
        {
            Name = row.Name,
            Cron = row.Cron,
            TimeZone = row.TimeZone,
            PinnedVersion = row.PinnedVersion,
            Inputs = StoredJson.Object(row.Inputs) ?? [],
            IsEnabled = row.IsEnabled,
        },
        NextRunAt = row.NextRunAt,
        LastFiredAt = row.LastFiredAt,
        LastRunId = row.LastRunId is { } runId ? new RunId(runId) : null,
        LastError = row.LastError,
        RowVersion = row.RowVersion,
        Authorship = new Authorship(row.CreatedAt, row.CreatedBy, row.UpdatedAt, row.UpdatedBy),
    };
}
