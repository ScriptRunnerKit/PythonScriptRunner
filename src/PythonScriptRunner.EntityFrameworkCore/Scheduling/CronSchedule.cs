using Cronos;

namespace PythonScriptRunner.EntityFrameworkCore.Scheduling;

/// <summary>A parsed cron expression in an IANA time zone (Cronos; daylight saving time handled).</summary>
internal sealed class CronSchedule
{
    private readonly CronExpression _expression;
    private readonly TimeZoneInfo _zone;

    private CronSchedule(CronExpression expression, TimeZoneInfo zone)
    {
        _expression = expression;
        _zone = zone;
    }

    /// <summary>Parses a 5-field (minutes) or 6-field (seconds) expression. <c>SCHEDULE_INVALID</c> when the expression or the zone is invalid.</summary>
    /// <param name="cron">Cron expression.</param>
    /// <param name="timeZone">IANA time zone.</param>
    public static CronSchedule Parse(string cron, string timeZone)
    {
        if (string.IsNullOrWhiteSpace(cron) || cron.Length > 100)
        {
            throw new PsrDomainException(PsrErrorCodes.InvalidCron, "The cron expression is required (up to 100 chars).");
        }

        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        CronExpression expression;
        try
        {
            expression = CronExpression.Parse(cron.Trim(), fields == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
        }
        catch (CronFormatException ex)
        {
            throw new PsrDomainException(PsrErrorCodes.InvalidCron, $"Invalid cron expression '{cron}': {ex.Message}", ex);
        }

        try
        {
            return new CronSchedule(expression, TimeZoneInfo.FindSystemTimeZoneById(timeZone));
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            throw new PsrDomainException(PsrErrorCodes.InvalidTimeZone, $"Unknown IANA time zone '{timeZone}'.", ex);
        }
    }

    /// <summary>The first occurrence strictly after <paramref name="after"/> (UTC), or <c>null</c> when there is none.</summary>
    /// <param name="after">Reference instant.</param>
    public DateTimeOffset? Next(DateTimeOffset after) =>
        _expression.GetNextOccurrence(after.ToUniversalTime(), _zone) is { } next ? next.ToUniversalTime() : null;

    /// <summary>The next <paramref name="count"/> occurrences after <paramref name="after"/> (UTC).</summary>
    /// <param name="after">Reference instant.</param>
    /// <param name="count">How many.</param>
    public IReadOnlyList<DateTimeOffset> Next(DateTimeOffset after, int count)
    {
        var occurrences = new List<DateTimeOffset>(count);
        var from = after;
        while (occurrences.Count < count && Next(from) is { } next)
        {
            occurrences.Add(next);
            from = next;
        }

        return occurrences;
    }

    /// <summary>The time zone of the expression.</summary>
    public TimeZoneInfo Zone => _zone;

    /// <summary>How many occurrences fall in <c>[from, to]</c>, counting up to <paramref name="max"/>.</summary>
    /// <param name="from">First instant (inclusive).</param>
    /// <param name="to">Last instant (inclusive).</param>
    /// <param name="max">Stops counting here (a schedule every second missed for days has millions).</param>
    public int CountOccurrences(DateTimeOffset from, DateTimeOffset to, int max = 100_000) =>
        to < from ? 0 : _expression.GetOccurrences(from.ToUniversalTime(), to.ToUniversalTime(), _zone, fromInclusive: true, toInclusive: true).Take(max).Count();

}
