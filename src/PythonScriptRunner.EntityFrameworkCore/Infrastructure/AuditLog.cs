using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using PythonScriptRunner.EntityFrameworkCore.Catalog;
using PythonScriptRunner.EntityFrameworkCore.Entities;

namespace PythonScriptRunner.EntityFrameworkCore.Infrastructure;

/// <summary>Adds audit events to a context, so they are saved in the same transaction as the change they describe.</summary>
internal static class AuditLog
{
    /// <summary>Adds the event when the actor is human (automatic callers are recorded on the run itself; D-93).</summary>
    public static void Add(PsrDbContext db, AuditEntry entry, DateTimeOffset now)
    {
        if (!entry.Actor.IsHuman)
        {
            return;
        }

        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.CreateVersion7(now),
            OccurredAt = now,
            Actor = entry.Actor.Subject,
            Action = entry.Action,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            EntityKey = entry.EntityKey,
            Details = entry.Details is { Count: > 0 } details ? details.ToJsonString() : null,
            TraceId = Activity.Current?.TraceId.ToHexString(),
        });
    }
}

/// <summary>Writes audit events in their own transaction.</summary>
internal sealed class AuditWriter(IDbContextFactory<PsrDbContext> factory, TimeProvider time) : IAuditWriter
{
    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
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
}

/// <summary>Lists audit events newest first (keyset on occurred_at, id).</summary>
internal sealed class AuditReader(IDbContextFactory<PsrDbContext> factory, TimeProvider time) : IAuditReader
{
    public async Task<KeysetPage<AuditEvent>> ListAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (from, to) = TimeWindow.Resolve(query.From, query.To, time);
        var limit = Math.Clamp(query.Limit, 1, 100);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var events = db.AuditEvents.AsNoTracking().Where(e => e.OccurredAt >= from && e.OccurredAt < to);
            if (query.Actor is { } actor)
            {
                events = events.Where(e => e.Actor == actor);
            }

            if (query.EntityType is { } entityType)
            {
                events = events.Where(e => e.EntityType == entityType);
            }

            if (KeysetCursor.Decode(query.Cursor) is { } cursor)
            {
                events = events.Where(e => e.OccurredAt < cursor.At || (e.OccurredAt == cursor.At && e.Id < cursor.Id));
            }

            var rows = await events.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Take(limit + 1)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var page = rows.Take(limit)
                .Select(e => new AuditEvent(e.Id, e.OccurredAt, e.Actor, e.Action, e.EntityType, e.EntityId, e.EntityKey, StoredJson.Object(e.Details), e.TraceId))
                .ToList();
            return new KeysetPage<AuditEvent>(page, rows.Count > limit ? KeysetCursor.Encode(rows[limit - 1].OccurredAt, rows[limit - 1].Id) : null);
        }
    }
}

/// <summary>Time window of history queries: default last 24 hours, at most 90 days (keeps queries on recent partitions).</summary>
internal static class TimeWindow
{
    public static readonly TimeSpan Default = TimeSpan.FromHours(24);
    public static readonly TimeSpan Maximum = TimeSpan.FromDays(90);

    public static (DateTimeOffset From, DateTimeOffset To) Resolve(DateTimeOffset? from, DateTimeOffset? to, TimeProvider time)
    {
        var end = to ?? time.GetUtcNow();
        var start = from ?? end - Default;
        if (start >= end)
        {
            throw new ArgumentException("The window must start before it ends.", nameof(from));
        }

        return end - start > Maximum
            ? throw new ArgumentException($"The window cannot exceed {Maximum.TotalDays:0} days.", nameof(from))
            : (start, end);
    }
}

/// <summary>Opaque cursor <c>(timestamp, id)</c> of keyset pagination.</summary>
internal static class KeysetCursor
{
    public static string Encode(DateTimeOffset at, Guid id) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{at.UtcTicks}:{id:N}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static (DateTimeOffset At, Guid Id)? Decode(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
            var parts = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64)).Split(':');
            return (new DateTimeOffset(long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero), Guid.ParseExact(parts[1], "N"));
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new ArgumentException("Invalid cursor.", nameof(cursor), ex);
        }
    }
}
