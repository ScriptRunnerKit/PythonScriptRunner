namespace PythonScriptRunner;

/// <summary>What a run executes.</summary>
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

/// <summary>Classification of a failure.</summary>
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
