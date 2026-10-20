using PythonScriptRunner.EntityFrameworkCore.Scheduling;

namespace PythonScriptRunner.EntityFrameworkCore;

/// <summary>Next occurrences of a cron expression in an IANA time zone (the schedule form preview).</summary>
public static class SchedulePreview
{
    /// <summary>Largest preview.</summary>
    public const int MaxCount = 20;

    /// <summary>The next occurrences after <paramref name="after"/>. <c>INVALID_CRON</c> or <c>INVALID_TIME_ZONE</c> when invalid.</summary>
    /// <param name="cron">Cron expression (5 or 6 fields).</param>
    /// <param name="timeZone">IANA time zone.</param>
    /// <param name="after">Reference instant.</param>
    /// <param name="count">How many (1-20).</param>
    public static IReadOnlyList<ScheduleOccurrence> Next(string cron, string timeZone, DateTimeOffset after, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxCount);
        var schedule = CronSchedule.Parse(cron, timeZone);
        return [.. schedule.Next(after, count).Select(utc => new ScheduleOccurrence(utc, TimeZoneInfo.ConvertTime(utc, schedule.Zone)))];
    }
}

/// <summary>One occurrence of a schedule.</summary>
/// <param name="Utc">Instant in UTC.</param>
/// <param name="Local">Same instant in the schedule time zone (with its offset).</param>
public sealed record ScheduleOccurrence(DateTimeOffset Utc, DateTimeOffset Local);
