namespace PythonScriptRunner;

/// <summary>Stable codes of the domain errors raised by catalog and run management (mapped to HTTP by the dashboard).</summary>
public static class PsrErrorCodes
{
    /// <summary>A stage with this key already exists.</summary>
    public const string StageKeyTaken = "STAGE_KEY_TAKEN";

    /// <summary>A pipeline with this key already exists.</summary>
    public const string PipelineKeyTaken = "PIPELINE_KEY_TAKEN";

    /// <summary>The row changed since it was read (optimistic concurrency).</summary>
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";

    /// <summary>Published and archived versions cannot be edited.</summary>
    public const string VersionImmutable = "VERSION_IMMUTABLE";

    /// <summary>The stage version is used by a published pipeline.</summary>
    public const string VersionReferenced = "VERSION_REFERENCED";

    /// <summary>The pipeline version is pinned by an enabled schedule.</summary>
    public const string VersionScheduled = "VERSION_SCHEDULED";

    /// <summary>Only one draft per stage or pipeline is allowed.</summary>
    public const string DraftAlreadyExists = "DRAFT_ALREADY_EXISTS";

    /// <summary>The stage definition is invalid (fields, limits, secrets).</summary>
    public const string StageInvalid = "STAGE_INVALID";

    /// <summary>The script does not compile.</summary>
    public const string ScriptSyntaxError = "SCRIPT_SYNTAX_ERROR";

    /// <summary>uv could not resolve the dependencies.</summary>
    public const string DependencyResolutionFailed = "DEPENDENCY_RESOLUTION_FAILED";

    /// <summary>The pipeline does not pass validation.</summary>
    public const string PipelineInvalid = "PIPELINE_INVALID";

    /// <summary>The stage or pipeline is referenced or has runs in the retained history.</summary>
    public const string DeleteNotAllowed = "DELETE_NOT_ALLOWED";

    /// <summary>A pipeline retention cannot exceed the global retention.</summary>
    public const string RetentionExceedsGlobal = "RETENTION_EXCEEDS_GLOBAL";

    /// <summary>Only failed runs can be reprocessed.</summary>
    public const string RunNotFailed = "RUN_NOT_FAILED";

    /// <summary>The run already finished.</summary>
    public const string RunNotCancellable = "RUN_NOT_CANCELLABLE";

    /// <summary>Files needed to reprocess the run are beyond the retention.</summary>
    public const string ArtifactsExpired = "ARTIFACTS_EXPIRED";

    /// <summary>The file content is beyond the retention.</summary>
    public const string ArtifactExpired = "ARTIFACT_EXPIRED";

    /// <summary>The imported archive is not a valid catalog folder.</summary>
    public const string ImportInvalid = "IMPORT_INVALID";

    /// <summary>The schedule does not exist.</summary>
    public const string ScheduleNotFound = "SCHEDULE_NOT_FOUND";

    /// <summary>The schedule targets a version that cannot run (missing, draft or archived).</summary>
    public const string ScheduleInvalid = "SCHEDULE_INVALID";

    /// <summary>The cron expression is invalid.</summary>
    public const string InvalidCron = "INVALID_CRON";

    /// <summary>The IANA time zone is unknown.</summary>
    public const string InvalidTimeZone = "INVALID_TIME_ZONE";

    /// <summary>Pipelines with file inputs cannot be scheduled nor started by the public queue command (D-89).</summary>
    public const string ScheduleFileInput = "SCHEDULE_FILE_INPUT";

    /// <summary>The pipeline has file inputs, which the public queue command cannot send (D-89).</summary>
    public const string FileInputNotAllowed = "FILE_INPUT_NOT_ALLOWED";
}

/// <summary>A business rule rejected the operation; <see cref="Code"/> is stable (see <see cref="PsrErrorCodes"/>).</summary>
public class PsrDomainException : PsrException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">Stable code.</param>
    /// <param name="message">Human-readable message.</param>
    /// <param name="innerException">Cause.</param>
    public PsrDomainException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Stable code (see <see cref="PsrErrorCodes"/>).</summary>
    public string Code { get; }
}

/// <summary>The row changed since it was read; <see cref="Current"/> holds the current state.</summary>
public sealed class ConcurrencyConflictException : PsrDomainException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What conflicted.</param>
    /// <param name="current">Current state (e.g. the current draft), when it still exists.</param>
    public ConcurrencyConflictException(string message, object? current)
        : base(PsrErrorCodes.ConcurrencyConflict, message)
    {
        Current = current;
    }

    /// <summary>Current state (e.g. <see cref="StageDraft"/>), or <c>null</c> when it no longer exists.</summary>
    public object? Current { get; }
}

/// <summary>The script of a stage does not compile.</summary>
public sealed class ScriptSyntaxException : PsrDomainException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Python error message.</param>
    /// <param name="line">1-based line, when known.</param>
    /// <param name="column">1-based column, when known.</param>
    public ScriptSyntaxException(string message, int? line, int? column)
        : base(PsrErrorCodes.ScriptSyntaxError, line is null ? message : $"{message} (line {line}, column {column})")
    {
        Line = line;
        Column = column;
    }

    /// <summary>1-based line, when known.</summary>
    public int? Line { get; }

    /// <summary>1-based column, when known.</summary>
    public int? Column { get; }
}

/// <summary>A definition does not pass validation; <see cref="Report"/> lists the problems.</summary>
public sealed class DefinitionInvalidException : PsrDomainException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code"><see cref="PsrErrorCodes.PipelineInvalid"/> or <see cref="PsrErrorCodes.StageInvalid"/>.</param>
    /// <param name="report">Validation report.</param>
    public DefinitionInvalidException(string code, ValidationReport report)
        : base(code, $"Validation failed: {report}")
    {
        Report = report;
    }

    /// <summary>Validation report.</summary>
    public ValidationReport Report { get; }
}

/// <summary>The stage version is used by published pipelines and cannot be archived.</summary>
public sealed class VersionReferencedException : PsrDomainException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="key">Stage key.</param>
    /// <param name="version">Stage version.</param>
    /// <param name="pipelines">Pipelines that use it (<c>key@version</c>).</param>
    public VersionReferencedException(string key, int version, IReadOnlyList<string> pipelines)
        : base(PsrErrorCodes.VersionReferenced, $"Stage '{key}@{version}' is used by published pipelines: {string.Join(", ", pipelines)}.")
    {
        Pipelines = pipelines;
    }

    /// <summary>Pipelines that use the version (<c>key@version</c>).</summary>
    public IReadOnlyList<string> Pipelines { get; }
}
