using System.Text.Json.Serialization;

namespace PythonScriptRunner;

/// <summary>What a run executes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RunKind>))]
public enum RunKind
{
    /// <summary>A published pipeline version.</summary>
    Pipeline,
    /// <summary>A pipeline draft tested from the panel.</summary>
    PipelineTest,
    /// <summary>A single published stage.</summary>
    Stage,
    /// <summary>A stage draft tested from the panel.</summary>
    StageTest,
}

/// <summary>Status of a run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RunStatus>))]
public enum RunStatus
{
    /// <summary>Waiting for a concurrency slot of the pipeline.</summary>
    Pending,
    /// <summary>Waiting in the queue.</summary>
    Queued,
    /// <summary>Executing.</summary>
    Running,
    /// <summary>Finished successfully.</summary>
    Succeeded,
    /// <summary>Finished with an error (can be reprocessed).</summary>
    Failed,
    /// <summary>Cancellation requested; processes are being stopped.</summary>
    Cancelling,
    /// <summary>Cancelled.</summary>
    Cancelled,
}

/// <summary>Status of a step within a run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StepStatus>))]
public enum StepStatus
{
    /// <summary>Not started yet.</summary>
    Pending,
    /// <summary>Waiting in the queue.</summary>
    Queued,
    /// <summary>Executing.</summary>
    Running,
    /// <summary>Finished successfully.</summary>
    Succeeded,
    /// <summary>Failed without retries left.</summary>
    Failed,
    /// <summary>Exceeded its timeout without retries left.</summary>
    TimedOut,
    /// <summary>Cancelled.</summary>
    Cancelled,
    /// <summary>Not executed because the run ended earlier.</summary>
    Skipped,
}

/// <summary>Final status of one attempt.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AttemptStatus>))]
public enum AttemptStatus
{
    /// <summary>Executing.</summary>
    Running,
    /// <summary>Finished successfully.</summary>
    Succeeded,
    /// <summary>Failed.</summary>
    Failed,
    /// <summary>Exceeded its timeout.</summary>
    TimedOut,
    /// <summary>Cancelled.</summary>
    Cancelled,
    /// <summary>The worker stopped renewing its lease.</summary>
    WorkerLost,
}

/// <summary>Why an attempt was started.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AttemptCause>))]
public enum AttemptCause
{
    /// <summary>First attempt of the step.</summary>
    Initial,
    /// <summary>Automatic retry from the stage retry policy.</summary>
    AutoRetry,
    /// <summary>Manual reprocessing of a failed run.</summary>
    Reprocess,
    /// <summary>Recovery after a lost worker lease.</summary>
    LeaseRecovery,
}

/// <summary>Who or what started a run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RunTrigger>))]
public enum RunTrigger
{
    /// <summary>Code calling the library.</summary>
    Library,
    /// <summary>A user in the panel.</summary>
    Dashboard,
    /// <summary>A cron schedule.</summary>
    Schedule,
    /// <summary>The public queue command.</summary>
    QueueCommand,
}

/// <summary>How a run is executed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExecutionMode>))]
public enum ExecutionMode
{
    /// <summary>In the calling process, waiting for the result.</summary>
    Sync,
    /// <summary>By a worker, through the queue.</summary>
    Async,
}

/// <summary>Life cycle of a stage or pipeline version.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VersionStatus>))]
public enum VersionStatus
{
    /// <summary>Editable; can be tested but not used by published pipelines.</summary>
    Draft,
    /// <summary>Immutable and runnable.</summary>
    Published,
    /// <summary>Immutable and no longer runnable (terminal).</summary>
    Archived,
}

/// <summary>Whether a stored file was given to a run or produced by it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ArtifactDirection>))]
public enum ArtifactDirection
{
    /// <summary>Input of the run.</summary>
    Input,
    /// <summary>Output of a step.</summary>
    Output,
}

/// <summary>Output stream of a Python process.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LogStream>))]
public enum LogStream
{
    /// <summary>Standard output.</summary>
    Stdout,
    /// <summary>Standard error.</summary>
    Stderr,
}

/// <summary>Classification of a failure.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ErrorKind>))]
public enum ErrorKind
{
    /// <summary>Inputs do not match the contract.</summary>
    InputValidation,
    /// <summary>Outputs do not match the contract (extra, missing or wrong type).</summary>
    OutputContractViolation,
    /// <summary>The script raised an error (including <c>StageError</c>).</summary>
    ScriptError,
    /// <summary>The attempt exceeded its timeout.</summary>
    Timeout,
    /// <summary>The process exceeded its memory limit.</summary>
    MemoryLimitExceeded,
    /// <summary>The run was cancelled.</summary>
    Cancelled,
    /// <summary>The worker executing the step was lost.</summary>
    WorkerLost,
    /// <summary>The Python environment could not be created.</summary>
    EnvironmentSetupFailed,
    /// <summary>A file exceeded the size limits.</summary>
    ArtifactLimitExceeded,
    /// <summary>A declared secret could not be resolved.</summary>
    SecretNotFound,
    /// <summary>The process ended without a valid <c>output.json</c>.</summary>
    ProcessCrashed,
    /// <summary>The run reached the retention limit before finishing.</summary>
    Expired,
}
