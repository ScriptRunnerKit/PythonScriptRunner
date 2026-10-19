using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PythonScriptRunner.Core.Diagnostics;
using PythonScriptRunner.Core.Execution;
using PythonScriptRunner.Core.Pipelines;
using PythonScriptRunner.Core.Values;

namespace PythonScriptRunner.Core.Running;

/// <summary>
/// Runs stages and pipelines in the current process: validation, secrets, environment,
/// attempts with retry and, for pipelines, bindings between steps.
/// </summary>
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

        return await RunAsync(RunKind.Stage, stage.Key, stage.Version, bound, options, settings, async (context, inputFiles) =>
        {
            var step = await ExecuteStepAsync(stage, stage.Key, 1, bound.Wire, ToCopies(inputFiles), context, cancellationToken).ConfigureAwait(false);
            return new RunOutcome(StatusOf(step.Status), [step], step.Outputs, step.Files, step.Error);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunResult> RunPipelineAsync(
        string pipelineKey, object? inputs, RunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineKey);
        var settings = Settings;
        _janitor.EnsureCleaned();
        var pipeline = await ResolvePipelineAsync(pipelineKey, options?.Version, cancellationToken).ConfigureAwait(false);
        var bound = InputBinder.Bind(pipeline.Inputs, inputs, settings.MaxFileBytes);
        cancellationToken.ThrowIfCancellationRequested();

        return await RunAsync(RunKind.Pipeline, pipeline.Key, pipeline.Version, bound, options, settings,
            (context, inputFiles) => ExecutePipelineAsync(pipeline, bound.Wire, inputFiles, context, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_concurrency.IsValueCreated)
        {
            _concurrency.Value.Dispose();
        }
    }

    private async Task<RunResult> RunAsync(
        RunKind kind,
        string key,
        int version,
        BoundInputs bound,
        RunOptions? options,
        PythonScriptRunnerOptions settings,
        Func<RunContext, IReadOnlyDictionary<string, IReadOnlyList<PsrFile>>, Task<RunOutcome>> body,
        CancellationToken cancellationToken)
    {
        var runId = RunId.New();
        var started = _time.GetTimestamp();
        await _recorder.RunStartedAsync(
            new RunStarted(runId, kind, key, version, options?.TriggeredBy ?? "system:library", _time.GetUtcNow(), (JsonObject)bound.Wire.DeepClone()),
            cancellationToken).ConfigureAwait(false);

        var context = new RunContext(new RunWorkspace(settings.WorkRoot, runId), settings);
        try
        {
            RunOutcome outcome;
            try
            {
                var inputFiles = await MaterializeInputsAsync(bound.Files, context.Workspace, cancellationToken).ConfigureAwait(false);
                context.UsedBytes = inputFiles.Values.SelectMany(files => files).Sum(file => file.Length ?? 0);
                outcome = context.UsedBytes > settings.MaxRunFilesBytes
                    ? RunOutcome.Aborted(RunStatus.Failed, new RunError(ErrorKind.ArtifactLimitExceeded, "The input files exceed the run limit.") { Code = "RUN_FILES_TOO_LARGE" })
                    : await body(context, inputFiles).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcome = RunOutcome.Aborted(RunStatus.Cancelled, new RunError(ErrorKind.Cancelled, "The run was cancelled."));
            }

            await _recorder.RunFinishedAsync(new RunFinished(runId, outcome.Status, _time.GetUtcNow(), outcome.Outputs, outcome.Error), CancellationToken.None)
                .ConfigureAwait(false);
            return new RunResult(runId, kind, outcome.Status, _time.GetElapsedTime(started), outcome.Steps, outcome.Outputs, outcome.Files, outcome.Error,
                context.Workspace.DeleteResultsAsync);
        }
        catch
        {
            await context.Workspace.DeleteResultsAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            context.Workspace.DeleteRun();
        }
    }

    private async Task<RunOutcome> ExecutePipelineAsync(
        PipelineDefinition pipeline,
        JsonObject pipelineInputs,
        IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> pipelineFiles,
        RunContext context,
        CancellationToken cancellationToken)
    {
        var steps = new List<StepResult>();
        var completed = new Dictionary<string, StepResult>(StringComparer.Ordinal);
        var status = RunStatus.Succeeded;
        RunError? error = null;
        for (var i = 0; i < pipeline.Steps.Count; i++)
        {
            var definition = pipeline.Steps[i];
            if (status != RunStatus.Succeeded)
            {
                steps.Add(Skipped(definition, i + 1));
                continue;
            }

            var stage = await _catalog.FindStageAsync(definition.StageKey, definition.StageVersion, cancellationToken).ConfigureAwait(false)
                ?? throw new PsrConfigurationException($"Stage '{definition.StageKey}@{definition.StageVersion}' of pipeline '{pipeline.Key}' is not in the catalog.");
            var inputs = StepInputResolver.Resolve(stage, pipeline, i, pipelineInputs, pipelineFiles, completed);
            var result = inputs.Errors.Count > 0
                ? InvalidInputs(stage, definition.Key, i + 1, inputs.Errors)
                : await ExecuteStepAsync(stage, definition.Key, i + 1, inputs.Wire, inputs.Files, context, cancellationToken).ConfigureAwait(false);
            steps.Add(result);
            if (result.Status == StepStatus.Succeeded)
            {
                completed[definition.Key] = result;
            }
            else
            {
                status = StatusOf(result.Status);
                error = result.Error;
            }
        }

        if (status != RunStatus.Succeeded)
        {
            return new RunOutcome(status, steps, null, null, error);
        }

        var outputs = new JsonObject();
        var files = new Dictionary<string, IReadOnlyList<PsrFile>>(StringComparer.Ordinal);
        foreach (var output in pipeline.Outputs)
        {
            var source = completed[output.Source.Step];
            if (source.Outputs?[output.Source.Field] is { } value)
            {
                outputs[output.Name] = value.DeepClone();
            }

            if (source.Files.TryGetValue(output.Source.Field, out var produced))
            {
                files[output.Name] = produced;
            }
        }

        return new RunOutcome(RunStatus.Succeeded, steps, outputs, files, null);
    }

    private async Task<StageDefinition> ResolveStageAsync(string key, int? version, CancellationToken cancellationToken)
    {
        if (await _catalog.FindStageAsync(key, version, cancellationToken).ConfigureAwait(false) is { } stage)
        {
            return stage;
        }

        if (version is { } requested && await _catalog.FindStageAsync(key, null, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new VersionNotFoundException(key, requested);
        }

        throw new StageNotFoundException(key);
    }

    private async Task<PipelineDefinition> ResolvePipelineAsync(string key, int? version, CancellationToken cancellationToken)
    {
        if (await _catalog.FindPipelineAsync(key, version, cancellationToken).ConfigureAwait(false) is { } pipeline)
        {
            return pipeline;
        }

        if (version is { } requested && await _catalog.FindPipelineAsync(key, null, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new VersionNotFoundException(key, requested);
        }

        throw new PipelineNotFoundException(key);
    }

    private async Task<StepResult> ExecuteStepAsync(
        StageDefinition stage,
        string stepKey,
        int order,
        JsonObject inputs,
        IReadOnlyList<InputFileCopy> files,
        RunContext context,
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

        for (var number = 1; ; number++)
        {
            var cause = number == 1 ? AttemptCause.Initial : AttemptCause.AutoRetry;
            var outcome = await RunAttemptAsync(stage, stepKey, number, cause, inputs, files, context, cancellationToken).ConfigureAwait(false);
            attempts.Add(outcome.Attempt);
            await _recorder.AttemptFinishedAsync(new AttemptFinished(context.Workspace.RunId, stepKey, outcome.Attempt), CancellationToken.None)
                .ConfigureAwait(false);

            switch (outcome.Attempt.Status)
            {
                case AttemptStatus.Succeeded:
                    context.UsedBytes += outcome.Files.Values.SelectMany(f => f).Sum(f => f.Length ?? 0);
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
        JsonObject inputs,
        IReadOnlyList<InputFileCopy> files,
        RunContext context,
        CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var startedAt = _time.GetUtcNow();
        var started = _time.GetTimestamp();
        var queued = TimeSpan.Zero;
        string? environmentHash = null;
        var workDirectory = context.Workspace.AttemptDirectory(stepKey, number);

        using var activity = PsrDiagnostics.Source.StartActivity("psr.stage.execute");
        activity?.SetTag("psr.stage.key", stage.Key);
        activity?.SetTag("psr.stage.version", stage.Version);
        activity?.SetTag("psr.run.id", context.Workspace.RunId.ToString());
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
                            RunId = context.Workspace.RunId,
                            StepKey = stepKey,
                            Attempt = number,
                            WorkDirectory = workDirectory,
                            Inputs = inputs,
                            Files = files,
                            Secrets = secrets,
                            Timeout = stage.Timeout ?? settings.DefaultTimeout,
                            MemoryLimitMb = stage.MemoryLimitMb ?? settings.DefaultMemoryLimitMb,
                            MaxFileBytes = settings.MaxFileBytes,
                            FileBudgetBytes = settings.MaxRunFilesBytes - context.UsedBytes,
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
                ? MoveOutputs(execution.Files, context.Workspace, stepKey)
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

    /// <summary>Copies the caller's files once to <c>runs/&lt;id&gt;/inputs</c>; every step and attempt reads them from there.</summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<PsrFile>>> MaterializeInputsAsync(
        IReadOnlyList<BoundFile> files, RunWorkspace workspace, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<PsrFile>>(StringComparer.Ordinal);
        foreach (var group in files.GroupBy(f => f.Field))
        {
            var list = new List<PsrFile>();
            foreach (var bound in group.OrderBy(f => f.Index))
            {
                var name = RunWorkspace.SafeFileName(bound.File.FileName);
                var target = Path.Combine(workspace.InputsDirectory, bound.Field, bound.Index.ToString(CultureInfo.InvariantCulture), name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await bound.File.SaveAsAsync(target, cancellationToken).ConfigureAwait(false);

                string sha256;
                await using (var stream = File.OpenRead(target))
                {
                    sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                }

                list.Add(PsrFile.FromProducedFile(target, name, bound.File.ContentType, sha256));
            }

            result[group.Key] = list;
        }

        return result;
    }

    private static List<InputFileCopy> ToCopies(IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> files) =>
        [.. files.SelectMany(group => group.Value.Select((file, index) =>
            new InputFileCopy(group.Key, index, file.LocalPath!, file.FileName, file.ContentType, file.Length ?? 0, file.Sha256!)))];

    private static Dictionary<string, IReadOnlyList<PsrFile>> MoveOutputs(IReadOnlyList<OutputFile> files, RunWorkspace workspace, string stepKey)
    {
        var result = new Dictionary<string, IReadOnlyList<PsrFile>>(StringComparer.Ordinal);
        foreach (var group in files.GroupBy(f => f.Field))
        {
            var list = new List<PsrFile>();
            foreach (var file in group.OrderBy(f => f.Index))
            {
                var name = RunWorkspace.SafeFileName(file.FileName);
                var target = Path.Combine(workspace.ResultsDirectory, stepKey, file.Field, file.Index.ToString(CultureInfo.InvariantCulture), name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file.FullPath, target, overwrite: true);
                list.Add(PsrFile.FromProducedFile(target, name, file.ContentType, file.Sha256));
            }

            result[group.Key] = list;
        }

        return result;
    }

    private static RunStatus StatusOf(StepStatus status) => status switch
    {
        StepStatus.Succeeded => RunStatus.Succeeded,
        StepStatus.Cancelled => RunStatus.Cancelled,
        _ => RunStatus.Failed,
    };

    private static StepResult Skipped(PipelineStepDefinition definition, int order) => new()
    {
        StepKey = definition.Key,
        Order = order,
        StageKey = definition.StageKey,
        StageVersion = definition.StageVersion,
        Status = StepStatus.Skipped,
        Attempts = [],
    };

    private static StepResult InvalidInputs(StageDefinition stage, string stepKey, int order, IReadOnlyList<InputError> errors) => new()
    {
        StepKey = stepKey,
        Order = order,
        StageKey = stage.Key,
        StageVersion = stage.Version,
        Status = StepStatus.Failed,
        Attempts = [],
        Error = new RunError(ErrorKind.InputValidation, string.Join("; ", errors.Select(e => $"'{e.Field}': {e.Message}")))
        {
            Code = errors[0].Code,
            StepKey = stepKey,
        },
    };

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

    private sealed record RunOutcome(
        RunStatus Status,
        IReadOnlyList<StepResult> Steps,
        JsonObject? Outputs,
        IReadOnlyDictionary<string, IReadOnlyList<PsrFile>>? Files,
        RunError? Error)
    {
        public static RunOutcome Aborted(RunStatus status, RunError error) => new(status, [], null, null, error);
    }

    /// <summary>State shared by the steps of one run (folders, settings and the file budget).</summary>
    private sealed class RunContext(RunWorkspace workspace, PythonScriptRunnerOptions settings)
    {
        public RunWorkspace Workspace { get; } = workspace;

        public PythonScriptRunnerOptions Settings { get; } = settings;

        public long UsedBytes { get; set; }
    }
}
