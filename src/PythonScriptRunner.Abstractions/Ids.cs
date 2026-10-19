namespace PythonScriptRunner;

/// <summary>Identifier of a run (time-ordered UUID v7).</summary>
/// <param name="Value">The underlying GUID.</param>
public readonly record struct RunId(Guid Value)
{
    /// <summary>Creates a new time-ordered identifier.</summary>
    public static RunId New() => new(Guid.CreateVersion7());

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
