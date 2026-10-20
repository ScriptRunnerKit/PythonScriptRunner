namespace PythonScriptRunner;

/// <summary>Who performs an operation: used for authorship columns and to decide auditing (only humans are audited).</summary>
/// <param name="Subject">User subject, or <c>system:&lt;origin&gt;</c> for automatic callers.</param>
/// <param name="IsHuman">Whether a person performed the action (panel); library, queue and cron are not.</param>
public sealed record Actor(string Subject, bool IsHuman)
{
    /// <summary>Code calling the library (<c>system:library</c>).</summary>
    public static Actor Library { get; } = System("library");

    /// <summary>An automatic caller.</summary>
    /// <param name="origin">Origin (e.g. <c>queue</c> or <c>schedule:&lt;id&gt;</c>).</param>
    public static Actor System(string origin) => new($"system:{origin}", IsHuman: false);

    /// <summary>A person (e.g. the subject of the authenticated user in the panel).</summary>
    /// <param name="subject">User subject.</param>
    public static Actor User(string subject) => new(subject, IsHuman: true);

    /// <inheritdoc />
    public override string ToString() => Subject;
}

/// <summary>Creation and last change of a row.</summary>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="CreatedBy">Creator subject.</param>
/// <param name="UpdatedAt">Last change.</param>
/// <param name="UpdatedBy">Subject of the last change.</param>
public sealed record Authorship(DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, string UpdatedBy);

/// <summary>One page of an offset-paginated list.</summary>
/// <typeparam name="T">Item type.</typeparam>
/// <param name="Items">Items of the page.</param>
/// <param name="PageNumber">1-based page number.</param>
/// <param name="PageSize">Requested page size.</param>
/// <param name="Total">Number of items in every page.</param>
public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int Total);

/// <summary>One page of a keyset-paginated list (newest first).</summary>
/// <typeparam name="T">Item type.</typeparam>
/// <param name="Items">Items of the page.</param>
/// <param name="NextCursor">Opaque cursor of the next page; <c>null</c> on the last page.</param>
public sealed record KeysetPage<T>(IReadOnlyList<T> Items, string? NextCursor);
