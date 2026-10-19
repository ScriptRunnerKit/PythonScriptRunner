namespace PythonScriptRunner;

/// <summary>Immutable definition of one version of a stage: inputs, one Python script and outputs.</summary>
public sealed record StageDefinition
{
    /// <summary>Unique slug of the stage (e.g. <c>html-to-pdf</c>).</summary>
    public required string Key { get; init; }

    /// <summary>Version number (1, 2, ...).</summary>
    public required int Version { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>The single Python script of the stage.</summary>
    public required ScriptSource Script { get; init; }

    /// <summary>Python version used to run the script (minimum 3.10); <c>null</c> uses the runner default (3.12).</summary>
    public string? PythonVersion { get; init; }

    /// <summary>Declared dependencies (requirements.txt format).</summary>
    public string? Requirements { get; init; }

    /// <summary>Locked dependencies with hashes; generated from <see cref="Requirements"/> when absent.</summary>
    public string? LockedRequirements { get; init; }

    /// <summary>Declared inputs.</summary>
    public IReadOnlyList<FieldDefinition> Inputs { get; init; } = [];

    /// <summary>Declared outputs.</summary>
    public IReadOnlyList<FieldDefinition> Outputs { get; init; } = [];

    /// <summary>Names of the secrets the script needs (e.g. <c>SMTP_PASSWORD</c>).</summary>
    public IReadOnlyList<string> RequiredSecrets { get; init; } = [];

    /// <summary>Maximum duration of one attempt; <c>null</c> uses the runner default (5 minutes).</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Memory limit of the Python process in MB; <c>null</c> uses the runner default (1024).</summary>
    public int? MemoryLimitMb { get; init; }

    /// <summary>Automatic retry policy. Default: no retry.</summary>
    public RetryPolicy Retry { get; init; } = RetryPolicy.None;

    /// <summary>Whether running the script twice with the same inputs is safe.</summary>
    public bool Idempotent { get; init; }
}

/// <summary>Where the script of a stage comes from.</summary>
public abstract record ScriptSource
{
    private protected ScriptSource()
    {
    }

    /// <summary>Script kept in memory (fluent API or database).</summary>
    /// <param name="content">Python source code.</param>
    public static ScriptSource Inline(string content) => new InlineScriptSource(content);

    /// <summary>Script read from a file (folder catalog).</summary>
    /// <param name="path">Path of the <c>.py</c> file.</param>
    public static ScriptSource FromFile(string path) => new FileScriptSource(System.IO.Path.GetFullPath(path));
}

/// <summary>Script kept in memory.</summary>
/// <param name="Content">Python source code.</param>
public sealed record InlineScriptSource(string Content) : ScriptSource;

/// <summary>Script stored in a file.</summary>
/// <param name="Path">Absolute path of the <c>.py</c> file.</param>
public sealed record FileScriptSource(string Path) : ScriptSource;

/// <summary>Automatic retry policy of a stage.</summary>
/// <param name="MaxAttempts">Number of retries after the first attempt (0 = no retry).</param>
/// <param name="InitialDelay">Delay before the first retry.</param>
/// <param name="BackoffMultiplier">Multiplier applied to the delay on each further retry.</param>
public sealed record RetryPolicy(int MaxAttempts, TimeSpan InitialDelay, double BackoffMultiplier)
{
    /// <summary>Upper bound of any retry delay (10 minutes).</summary>
    public static TimeSpan MaxDelay { get; } = TimeSpan.FromMinutes(10);

    /// <summary>No automatic retry (delay 10 s × 2 if retries are enabled later).</summary>
    public static RetryPolicy None { get; } = new(0, TimeSpan.FromSeconds(10), 2.0);

    /// <summary>Delay before retry number <paramref name="retryNumber"/> (1 = first retry).</summary>
    /// <param name="retryNumber">1-based retry number.</param>
    public TimeSpan GetDelay(int retryNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retryNumber, 1);
        var seconds = InitialDelay.TotalSeconds * Math.Pow(BackoffMultiplier, retryNumber - 1);
        return seconds >= MaxDelay.TotalSeconds ? MaxDelay : TimeSpan.FromSeconds(seconds);
    }
}
