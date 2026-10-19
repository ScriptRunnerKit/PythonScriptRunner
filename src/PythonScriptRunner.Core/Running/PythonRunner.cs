using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PythonScriptRunner.Core.Diagnostics;
using PythonScriptRunner.Core.Execution;
using PythonScriptRunner.Core.Values;

namespace PythonScriptRunner.Core.Running;

/// <summary>Runs stages in the current process: validation, secrets, environment, attempts with retry.</summary>
internal sealed class PythonRunner : IPythonRunner, IDisposable
{
    private readonly ICatalog _catalog;
    private readonly IEnvironmentManager _environments;
    private readonly IPythonExecutor _executor;
    private readonly ISecretProvider _secrets;
    private readonly IRunRecorder _recorder;
    private readonly IOptions<PythonScriptRunnerOptions> _options;
    private readonly PsrMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly WorkspaceJanitor _janitor;
    private readonly ILogger _logger;
    private readonly ILogger _scriptLogger;
    private readonly Lazy<SemaphoreSlim> _concurrency;

    public PythonRunner(
        ICatalog catalog,
        IEnvironmentManager environments,
        IPythonExecutor executor,
        ISecretProvider secrets,
        IRunRecorder recorder,
        IOptions<PythonScriptRunnerOptions> options,
        PsrMetrics metrics,
        TimeProvider time,
        WorkspaceJanitor janitor,
        ILoggerFactory loggerFactory)
    {
        _catalog = catalog;
        _environments = environments;
        _executor = executor;
        _secrets = secrets;
        _recorder = recorder;
        _options = options;
        _metrics = metrics;
        _time = time;
        _janitor = janitor;
        _logger = loggerFactory.CreateLogger<PythonRunner>();
        _scriptLogger = loggerFactory.CreateLogger("PythonScriptRunner.Script");
        _concurrency = new(() => new SemaphoreSlim(Settings.MaxConcurrentProcesses));
    }

    private PythonScriptRunnerOptions Settings
    {
        get
        {
            try
            {
                return _options.Value;
            }
            catch (OptionsValidationException ex)
            {
                throw new PsrConfigurationException(ex.Message, ex);
            }
        }
    }

    public async Task<RunResult> RunStageAsync(
        string stageKey, object? inputs, RunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageKey);
        var settings = Settings;
        _janitor.EnsureCleaned();
        var stage = await ResolveStageAsync(stageKey, options?.Version, cancellationToken).ConfigureAwait(false);
        var bound = InputBinder.Bind(stage.Inputs, inputs, settings.MaxFileBytes);
        cancellationToken.ThrowIfCancellationRequested();

        var runId = RunId.New();
        var started = _time.GetTimestamp();
        await _recorder.RunStartedAsync(
            new RunStarted(runId, RunKind.Stage, stage.Key, stage.Version, options?.TriggeredBy ?? "system:library", _time.GetUtcNow(), (JsonObject)bound.Wire.DeepClone()),
            cancellationToken).ConfigureAwait(false);

        var workspace = new RunWorkspace(settings.WorkRoot, runId);
        try
        {
            var step = await ExecuteStepAsync(stage, stage.Key, 1, bound, workspace, runId, settings, cancellationToken).ConfigureAwait(false);
            var status = step.Status switch
            {
                StepStatus.Succeeded => RunStatus.Succeeded,
                StepStatus.Cancelled => RunStatus.Cancelled,
                _ => RunStatus.Failed,
            };
            await _recorder.RunFinishedAsync(new RunFinished(runId, status, _time.GetUtcNow(), step.Outputs, step.Error), CancellationToken.None)
                .ConfigureAwait(false);
            return new RunResult(runId, RunKind.Stage, status, _time.GetElapsedTime(started), [step], step.Outputs, step.Files, step.Error, workspace.DeleteResultsAsync);
        }
        catch
        {
            await workspace.DeleteResultsAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            workspace.DeleteRun();
        }
    }

    public void Dispose()
    {
        if (_concurrency.IsValueCreated)
        {
            _concurrency.Value.Dispose();
        }
    }

    private async Task<StageDefinition> ResolveStageAsync(string key, int? version, CancellationToken cancellationToken)
    {
        var stage = await _catalog.FindStageAsync(key, version, cancellationToken).ConfigureAwait(false);
        if (stage is not null)
        {
            return stage;
        }

        if (version is { } requested && await _catalog.FindStageAsync(key, null, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new VersionNotFoundException(key, requested);
        }

        throw new StageNotFoundException(key);
    }

    private async Task<StepResult> ExecuteStepAsync(
        StageDefinition stage,
        string stepKey,
        int order,
        BoundInputs bound,
        RunWorkspace workspace,
        RunId runId,
        PythonScriptRunnerOptions settings,
        CancellationToken cancellationToken)
    {
        var attempts = new List<AttemptResult>();
        StepResult Finish(StepStatus status, RunError? error, AttemptOutcome? success = null) => new()
        {
            StepKey = stepKey,
            Order = order,
            StageKey = stage.Key,
            StageVersion = stage.Version,
            Status = status,
            Attempts = attempts,
            Outputs = success?.Outputs,
            Files = success?.Files ?? new Dictionary<string, IReadOnlyList<PsrFile>>(),
            Error = error is null ? null : error with { StepKey = stepKey },
        };

        IReadOnlyList<InputFileCopy> files;
        try
        {
            files = await MaterializeInputsAsync(bound.Files, workspace, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Finish(StepStatus.Cancelled, new RunError(ErrorKind.Cancelled, "The run was cancelled."));
        }

        var inputBytes = files.Sum(f => f.Size);
        if (inputBytes > settings.MaxRunFilesBytes)
        {
            return Finish(StepStatus.Failed, new RunError(ErrorKind.ArtifactLimitExceeded, "The input files exceed the run limit.") { Code = "RUN_FILES_TOO_LARGE" });
        }

        for (var number = 1; ; number++)
        {
            var cause = number == 1 ? AttemptCause.Initial : AttemptCause.AutoRetry;
            var outcome = await RunAttemptAsync(stage, stepKey, number, cause, bound, files, workspace, runId, settings, settings.MaxRunFilesBytes - inputBytes, cancellationToken)
                .ConfigureAwait(false);
            attempts.Add(outcome.Attempt);
            await _recorder.AttemptFinishedAsync(new AttemptFinished(runId, stepKey, outcome.Attempt), CancellationToken.None).ConfigureAwait(false);

            switch (outcome.Attempt.Status)
            {
                case AttemptStatus.Succeeded:
                    return Finish(StepStatus.Succeeded, null, outcome);
                case AttemptStatus.Cancelled:
                    return Finish(StepStatus.Cancelled, outcome.Attempt.Error);
            }

            var error = outcome.Attempt.Error!;
            Log.AttemptFailed(_logger, stage.Key, number, error.Kind, error.Message);
            if (number > stage.Retry.MaxAttempts)
            {
                return Finish(outcome.Attempt.Status == AttemptStatus.TimedOut ? StepStatus.TimedOut : StepStatus.Failed, error);
            }

            var delay = stage.Retry.GetDelay(number);
            Log.RetryScheduled(_logger, stage.Key, delay, number + 1);
            try
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Finish(StepStatus.Cancelled, new RunError(ErrorKind.Cancelled, "The run was cancelled while waiting to retry."));
            }
        }
    }

    private async Task<AttemptOutcome> RunAttemptAsync(
        StageDefinition stage,
        string stepKey,
        int number,
        AttemptCause cause,
        BoundInputs bound,
        IReadOnlyList<InputFileCopy> files,
        RunWorkspace workspace,
        RunId runId,
        PythonScriptRunnerOptions settings,
        long fileBudget,
        CancellationToken cancellationToken)
    {
        var startedAt = _time.GetUtcNow();
        var started = _time.GetTimestamp();
        var queued = TimeSpan.Zero;
        string? environmentHash = null;
        var workDirectory = workspace.AttemptDirectory(stepKey, number);

        using var activity = PsrDiagnostics.Source.StartActivity("psr.stage.execute");
        activity?.SetTag("psr.stage.key", stage.Key);
        activity?.SetTag("psr.stage.version", stage.Version);
        activity?.SetTag("psr.run.id", runId.ToString());
        activity?.SetTag("psr.step.key", stepKey);
        activity?.SetTag("psr.attempt", number);

        ExecutionResult execution;
        try
        {
            var (secrets, missing) = await ResolveSecretsAsync(stage, cancellationToken).ConfigureAwait(false);
            if (missing.Count > 0)
            {
                execution = ExecutionResult.Failure(ErrorKind.SecretNotFound, $"Secrets not configured: {string.Join(", ", missing)}.", "SECRET_NOT_FOUND");
            }
            else
            {
                var environment = await _environments.EnsureAsync(stage, cancellationToken).ConfigureAwait(false);
                environmentHash = environment.Hash;
                var waitStarted = _time.GetTimestamp();
                await _concurrency.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                queued = _time.GetElapsedTime(waitStarted);
                try
                {
                    execution = await _executor.ExecuteAsync(
                        new ExecutionRequest
                        {
                            Stage = stage,
                            Environment = environment,
                            RunId = runId,
                            StepKey = stepKey,
                            Attempt = number,
                            WorkDirectory = workDirectory,
                            Inputs = bound.Wire,
                            Files = files,
                            Secrets = secrets,
                            Timeout = stage.Timeout ?? settings.DefaultTimeout,
                            MemoryLimitMb = stage.MemoryLimitMb ?? settings.DefaultMemoryLimitMb,
                            MaxFileBytes = settings.MaxFileBytes,
                            FileBudgetBytes = fileBudget,
                            MaxLogBytes = settings.MaxLogBytes,
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _concurrency.Value.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            execution = ExecutionResult.Failure(ErrorKind.Cancelled, "The run was cancelled.", status: AttemptStatus.Cancelled);
        }
        catch (PythonEnvironmentException ex)
        {
            execution = ExecutionResult.Failure(ErrorKind.EnvironmentSetupFailed, ex.Message, "ENVIRONMENT_SETUP_FAILED");
        }

        try
        {
            var outputFiles = execution.Status == AttemptStatus.Succeeded
                ? MoveOutputs(execution.Files, workspace, stepKey)
                : new Dictionary<string, IReadOnlyList<PsrFile>>();
            var duration = _time.GetElapsedTime(started);
            var overhead = duration - execution.ProcessDuration - queued;
            var attempt = new AttemptResult
            {
                Id = AttemptId.New(),
                Number = number,
                Cause = cause,
                Status = execution.Status,
                StartedAt = startedAt,
                Duration = duration,
                Overhead = overhead < TimeSpan.Zero ? TimeSpan.Zero : overhead,
                ExitCode = execution.ExitCode,
                Error = execution.Error,
                Stdout = execution.Stdout,
                Stderr = execution.Stderr,
                EnvironmentHash = environmentHash,
            };

            ForwardOutput(stage.Key, attempt);
            _metrics.AttemptFinished(stage.Key, attempt);
            activity?.SetStatus(attempt.Status == AttemptStatus.Succeeded ? ActivityStatusCode.Ok : ActivityStatusCode.Error, attempt.Error?.Message);
            return new AttemptOutcome(attempt, execution.Outputs, outputFiles);
        }
        finally
        {
            RunWorkspace.TryDelete(workDirectory);
        }
    }

    private async Task<(IReadOnlyDictionary<string, string> Values, List<string> Missing)> ResolveSecretsAsync(
        StageDefinition stage, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var name in stage.RequiredSecrets)
        {
            var value = await _secrets.GetSecretAsync(name, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(value))
            {
                missing.Add(name);
            }
            else
            {
                values[name] = value;
            }
        }

        return (values, missing);
    }

    private static async Task<IReadOnlyList<InputFileCopy>> MaterializeInputsAsync(
        IReadOnlyList<BoundFile> files, RunWorkspace workspace, CancellationToken cancellationToken)
    {
        var copies = new List<InputFileCopy>(files.Count);
        foreach (var bound in files)
        {
            var name = RunWorkspace.SafeFileName(bound.File.FileName);
            var target = Path.Combine(workspace.InputsDirectory, bound.Field, bound.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await bound.File.SaveAsAsync(target, cancellationToken).ConfigureAwait(false);

            string sha256;
            long size;
            await using (var stream = File.OpenRead(target))
            {
                size = stream.Length;
                sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            }

            copies.Add(new InputFileCopy(bound.Field, bound.Index, target, name, bound.File.ContentType, size, sha256));
        }

        return copies;
    }

    private static Dictionary<string, IReadOnlyList<PsrFile>> MoveOutputs(IReadOnlyList<OutputFile> files, RunWorkspace workspace, string stepKey)
    {
        var result = new Dictionary<string, IReadOnlyList<PsrFile>>(StringComparer.Ordinal);
        foreach (var group in files.GroupBy(f => f.Field))
        {
            var list = new List<PsrFile>();
            foreach (var file in group.OrderBy(f => f.Index))
            {
                var name = RunWorkspace.SafeFileName(file.FileName);
                var target = Path.Combine(workspace.ResultsDirectory, stepKey, file.Field, file.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file.FullPath, target, overwrite: true);
                list.Add(PsrFile.FromProducedFile(target, name, file.ContentType, file.Sha256));
            }

            result[group.Key] = list;
        }

        return result;
    }

    private void ForwardOutput(string stageKey, AttemptResult attempt)
    {
        if (attempt.Stdout.Content.Length > 0)
        {
            Log.ScriptStdout(_scriptLogger, stageKey, attempt.Number, attempt.Stdout.Content);
        }

        if (attempt.Stderr.Content.Length > 0)
        {
            Log.ScriptStderr(_scriptLogger, stageKey, attempt.Number, attempt.Stderr.Content);
        }
    }

    private sealed record AttemptOutcome(AttemptResult Attempt, JsonObject? Outputs, IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> Files);
}
