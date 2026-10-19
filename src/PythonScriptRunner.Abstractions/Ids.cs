namespace PythonScriptRunner;

/// <summary>Identifier of a run (time-ordered UUID v7). Its timestamp is the creation time of the run.</summary>
/// <param name="Value">The underlying GUID.</param>
public readonly record struct RunId(Guid Value)
{
    /// <summary>Creates a new time-ordered identifier.</summary>
    public static RunId New() => new(Guid.CreateVersion7());

    /// <summary>Creates a new identifier whose timestamp is <paramref name="createdAt"/> (millisecond precision).</summary>
    /// <param name="createdAt">Creation time of the run.</param>
    public static RunId New(DateTimeOffset createdAt) => new(Guid.CreateVersion7(createdAt));

    /// <summary>Parses the canonical text form.</summary>
    /// <param name="text">GUID text.</param>
    public static RunId Parse(string text) => new(Guid.Parse(text));

    /// <summary>Creation time embedded in the identifier (UTC, millisecond precision).</summary>
    public DateTimeOffset CreatedAt => UuidV7.Timestamp(Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of one attempt of a step (time-ordered UUID v7).</summary>
/// <param name="Value">The underlying GUID.</param>
public readonly record struct AttemptId(Guid Value)
{
    /// <summary>Creates a new time-ordered identifier.</summary>
    public static AttemptId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of a step within a run (time-ordered UUID v7).</summary>
/// <param name="Value">The underlying GUID.</param>
public readonly record struct StepRunId(Guid Value)
{
    /// <summary>Creates a new time-ordered identifier.</summary>
    public static StepRunId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of a stored file (time-ordered UUID v7).</summary>
/// <param name="Value">The underlying GUID.</param>
public readonly record struct ArtifactId(Guid Value)
{
    /// <summary>Creates a new time-ordered identifier.</summary>
    public static ArtifactId New() => new(Guid.CreateVersion7());

    /// <summary>Parses the canonical text form.</summary>
    /// <param name="text">GUID text.</param>
    public static ArtifactId Parse(string text) => new(Guid.Parse(text));

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of a stage (UUID v7).</summary>
/// <param name="Value">The underlying GUID.</param>
public readonly record struct StageId(Guid Value)
{
    /// <summary>Creates a new time-ordered identifier.</summary>
    public static StageId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of a stage version (UUID v7).</summary>
/// <param name="Value">The underlying GUID.</param>
public readonly record struct StageVersionId(Guid Value)
{
    /// <summary>Creates a new time-ordered identifier.</summary>
    public static StageVersionId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of a pipeline (UUID v7).</summary>
/// <param name="Value">The underlying GUID.</param>
public readonly record struct PipelineId(Guid Value)
{
    /// <summary>Creates a new time-ordered identifier.</summary>
    public static PipelineId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of a pipeline version (UUID v7).</summary>
/// <param name="Value">The underlying GUID.</param>
public readonly record struct PipelineVersionId(Guid Value)
{
    /// <summary>Creates a new time-ordered identifier.</summary>
    public static PipelineVersionId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of a schedule (UUID v7).</summary>
/// <param name="Value">The underlying GUID.</param>
public readonly record struct ScheduleId(Guid Value)
{
    /// <summary>Creates a new time-ordered identifier.</summary>
    public static ScheduleId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Reads the timestamp of a UUID v7.</summary>
internal static class UuidV7
{
    public static DateTimeOffset Timestamp(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        var milliseconds = 0L;
        for (var i = 0; i < 6; i++)
        {
            milliseconds = (milliseconds << 8) | bytes[i];
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }
}
