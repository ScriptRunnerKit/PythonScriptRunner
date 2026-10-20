using System.Text.Json.Serialization;

namespace PythonScriptRunner;

/// <summary>Identifier of a run (time-ordered UUID v7). Its timestamp is the creation time of the run.</summary>
/// <param name="Value">The underlying GUID.</param>
[JsonConverter(typeof(GuidIdJsonConverter<RunId>))]
public readonly record struct RunId(Guid Value) : IGuidId<RunId>
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

    /// <summary>Parses the GUID text (route and query binding).</summary>
    /// <param name="text">GUID text.</param>
    /// <param name="id">The identifier.</param>
    public static bool TryParse(string? text, out RunId id)
    {
        var parsed = Guid.TryParse(text, out var value);
        id = new RunId(value);
        return parsed;
    }

    static RunId IGuidId<RunId>.From(Guid value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of one attempt of a step (time-ordered UUID v7).</summary>
/// <param name="Value">The underlying GUID.</param>
[JsonConverter(typeof(GuidIdJsonConverter<AttemptId>))]
public readonly record struct AttemptId(Guid Value) : IGuidId<AttemptId>
{
    /// <summary>Creates a new time-ordered identifier.</summary>
    public static AttemptId New() => new(Guid.CreateVersion7());

    /// <summary>Parses the GUID text (route and query binding).</summary>
    /// <param name="text">GUID text.</param>
    /// <param name="id">The identifier.</param>
    public static bool TryParse(string? text, out AttemptId id)
    {
        var parsed = Guid.TryParse(text, out var value);
        id = new AttemptId(value);
        return parsed;
    }

    static AttemptId IGuidId<AttemptId>.From(Guid value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of a step within a run (time-ordered UUID v7).</summary>
/// <param name="Value">The underlying GUID.</param>
[JsonConverter(typeof(GuidIdJsonConverter<StepRunId>))]
public readonly record struct StepRunId(Guid Value) : IGuidId<StepRunId>
{
    /// <summary>Creates a new time-ordered identifier.</summary>
    public static StepRunId New() => new(Guid.CreateVersion7());

    /// <summary>Parses the GUID text (route and query binding).</summary>
    /// <param name="text">GUID text.</param>
    /// <param name="id">The identifier.</param>
    public static bool TryParse(string? text, out StepRunId id)
    {
        var parsed = Guid.TryParse(text, out var value);
        id = new StepRunId(value);
        return parsed;
    }

    static StepRunId IGuidId<StepRunId>.From(Guid value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of a stored file (time-ordered UUID v7).</summary>
/// <param name="Value">The underlying GUID.</param>
[JsonConverter(typeof(GuidIdJsonConverter<ArtifactId>))]
public readonly record struct ArtifactId(Guid Value) : IGuidId<ArtifactId>
{
    /// <summary>Creates a new time-ordered identifier.</summary>
    public static ArtifactId New() => new(Guid.CreateVersion7());

    /// <summary>Parses the canonical text form.</summary>
    /// <param name="text">GUID text.</param>
    public static ArtifactId Parse(string text) => new(Guid.Parse(text));

    /// <summary>Parses the GUID text (route and query binding).</summary>
    /// <param name="text">GUID text.</param>
    /// <param name="id">The identifier.</param>
    public static bool TryParse(string? text, out ArtifactId id)
    {
        var parsed = Guid.TryParse(text, out var value);
        id = new ArtifactId(value);
        return parsed;
    }

    static ArtifactId IGuidId<ArtifactId>.From(Guid value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of a stage (UUID v7).</summary>
/// <param name="Value">The underlying GUID.</param>
[JsonConverter(typeof(GuidIdJsonConverter<StageId>))]
public readonly record struct StageId(Guid Value) : IGuidId<StageId>
{
    /// <summary>Creates a new time-ordered identifier.</summary>
    public static StageId New() => new(Guid.CreateVersion7());

    /// <summary>Parses the GUID text (route and query binding).</summary>
    /// <param name="text">GUID text.</param>
    /// <param name="id">The identifier.</param>
    public static bool TryParse(string? text, out StageId id)
    {
        var parsed = Guid.TryParse(text, out var value);
        id = new StageId(value);
        return parsed;
    }

    static StageId IGuidId<StageId>.From(Guid value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of a stage version (UUID v7).</summary>
/// <param name="Value">The underlying GUID.</param>
[JsonConverter(typeof(GuidIdJsonConverter<StageVersionId>))]
public readonly record struct StageVersionId(Guid Value) : IGuidId<StageVersionId>
{
    /// <summary>Creates a new time-ordered identifier.</summary>
    public static StageVersionId New() => new(Guid.CreateVersion7());

    /// <summary>Parses the GUID text (route and query binding).</summary>
    /// <param name="text">GUID text.</param>
    /// <param name="id">The identifier.</param>
    public static bool TryParse(string? text, out StageVersionId id)
    {
        var parsed = Guid.TryParse(text, out var value);
        id = new StageVersionId(value);
        return parsed;
    }

    static StageVersionId IGuidId<StageVersionId>.From(Guid value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of a pipeline (UUID v7).</summary>
/// <param name="Value">The underlying GUID.</param>
[JsonConverter(typeof(GuidIdJsonConverter<PipelineId>))]
public readonly record struct PipelineId(Guid Value) : IGuidId<PipelineId>
{
    /// <summary>Creates a new time-ordered identifier.</summary>
    public static PipelineId New() => new(Guid.CreateVersion7());

    /// <summary>Parses the GUID text (route and query binding).</summary>
    /// <param name="text">GUID text.</param>
    /// <param name="id">The identifier.</param>
    public static bool TryParse(string? text, out PipelineId id)
    {
        var parsed = Guid.TryParse(text, out var value);
        id = new PipelineId(value);
        return parsed;
    }

    static PipelineId IGuidId<PipelineId>.From(Guid value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of a pipeline version (UUID v7).</summary>
/// <param name="Value">The underlying GUID.</param>
[JsonConverter(typeof(GuidIdJsonConverter<PipelineVersionId>))]
public readonly record struct PipelineVersionId(Guid Value) : IGuidId<PipelineVersionId>
{
    /// <summary>Creates a new time-ordered identifier.</summary>
    public static PipelineVersionId New() => new(Guid.CreateVersion7());

    /// <summary>Parses the GUID text (route and query binding).</summary>
    /// <param name="text">GUID text.</param>
    /// <param name="id">The identifier.</param>
    public static bool TryParse(string? text, out PipelineVersionId id)
    {
        var parsed = Guid.TryParse(text, out var value);
        id = new PipelineVersionId(value);
        return parsed;
    }

    static PipelineVersionId IGuidId<PipelineVersionId>.From(Guid value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of a schedule (UUID v7).</summary>
/// <param name="Value">The underlying GUID.</param>
[JsonConverter(typeof(GuidIdJsonConverter<ScheduleId>))]
public readonly record struct ScheduleId(Guid Value) : IGuidId<ScheduleId>
{
    /// <summary>Creates a new time-ordered identifier.</summary>
    public static ScheduleId New() => new(Guid.CreateVersion7());

    /// <summary>Parses the GUID text (route and query binding).</summary>
    /// <param name="text">GUID text.</param>
    /// <param name="id">The identifier.</param>
    public static bool TryParse(string? text, out ScheduleId id)
    {
        var parsed = Guid.TryParse(text, out var value);
        id = new ScheduleId(value);
        return parsed;
    }

    static ScheduleId IGuidId<ScheduleId>.From(Guid value) => new(value);

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
