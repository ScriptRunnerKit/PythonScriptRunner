namespace PythonScriptRunner;

/// <summary>Validates pipeline definitions (used when loading catalogs, publishing and editing in the panel).</summary>
public interface IPipelineValidator
{
    /// <summary>Validates a pipeline against the stages of the catalog.</summary>
    /// <param name="pipeline">The definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Errors (block publication) and warnings.</returns>
    ValueTask<ValidationReport> ValidateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken = default);
}

/// <summary>Result of a pipeline validation.</summary>
/// <param name="Errors">Problems that block publication.</param>
/// <param name="Warnings">Problems that do not block publication.</param>
public sealed record ValidationReport(IReadOnlyList<ValidationIssue> Errors, IReadOnlyList<ValidationIssue> Warnings)
{
    /// <summary>Whether there are no errors.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <inheritdoc />
    public override string ToString() => string.Join("; ", Errors.Concat(Warnings).Select(i => i.ToString()));
}

/// <summary>One validation problem.</summary>
/// <param name="Code">Stable code (e.g. <c>PIPE_TYPE_MISMATCH</c>).</param>
/// <param name="Message">Human-readable message.</param>
public sealed record ValidationIssue(string Code, string Message)
{
    /// <summary>Step key, when the problem belongs to a step.</summary>
    public string? Step { get; init; }

    /// <summary>Field name (step input, pipeline input or output).</summary>
    public string? Field { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"[{Code}]{(Step is null ? "" : $" step '{Step}'")}{(Field is null ? "" : $" field '{Field}'")}: {Message}";
}

/// <summary>The pipeline does not exist in the catalog.</summary>
public sealed class PipelineNotFoundException : PsrException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="pipelineKey">Requested key.</param>
    public PipelineNotFoundException(string pipelineKey)
        : base($"Pipeline '{pipelineKey}' was not found.")
    {
        PipelineKey = pipelineKey;
    }

    /// <summary>Requested key.</summary>
    public string PipelineKey { get; }
}
