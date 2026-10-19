using System.Text.Json.Nodes;

namespace PythonScriptRunner.Core.Execution;

/// <summary>An input file already copied to the run folder.</summary>
internal sealed record InputFileCopy(string Field, int Index, string SourcePath, string FileName, string ContentType, long Size, string Sha256);

/// <summary>An output file produced by the script and validated by the runner.</summary>
internal sealed record OutputFile(string Field, int Index, string FullPath, string FileName, string ContentType, long Size, string Sha256);

/// <summary>Everything needed to run one attempt.</summary>
internal sealed record ExecutionRequest
{
    public required StageDefinition Stage { get; init; }

    public required PythonEnvironment Environment { get; init; }

    public required RunId RunId { get; init; }

    public required string StepKey { get; init; }

    public required int Attempt { get; init; }

    public bool IsTest { get; init; }

    public required string WorkDirectory { get; init; }

    /// <summary>Wire inputs; file fields hold placeholders that the executor completes.</summary>
    public required JsonObject Inputs { get; init; }

    public IReadOnlyList<InputFileCopy> Files { get; init; } = [];

    public IReadOnlyDictionary<string, string> Secrets { get; init; } = new Dictionary<string, string>();

    public required TimeSpan Timeout { get; init; }

    public required int MemoryLimitMb { get; init; }

    public required long MaxFileBytes { get; init; }

    /// <summary>Bytes still available for output files in this run.</summary>
    public required long FileBudgetBytes { get; init; }

    public required int MaxLogBytes { get; init; }
}

/// <summary>Outcome of one attempt as seen by the executor.</summary>
internal sealed record ExecutionResult
{
    public required AttemptStatus Status { get; init; }

    public RunError? Error { get; init; }

    public int? ExitCode { get; init; }

    public TimeSpan ProcessDuration { get; init; }

    public LogTail Stdout { get; init; } = LogTail.Empty;

    public LogTail Stderr { get; init; } = LogTail.Empty;

    /// <summary>Validated outputs in wire format (files as metadata).</summary>
    public JsonObject? Outputs { get; init; }

    public IReadOnlyList<OutputFile> Files { get; init; } = [];

    public static ExecutionResult Failure(ErrorKind kind, string message, string? code = null, AttemptStatus status = AttemptStatus.Failed) =>
        new() { Status = status, Error = new RunError(kind, message) { Code = code } };
}

/// <summary>Runs one attempt of a stage.</summary>
internal interface IPythonExecutor
{
    Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken);
}
