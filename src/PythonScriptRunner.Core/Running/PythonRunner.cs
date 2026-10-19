using System.Collections.Concurrent;
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
/// Runs stages and pipelines in the current process: validation, secrets, environment, attempts with retry and, for
/// pipelines, bindings between steps. Every transition is reported to the <see cref="IRunRecorder"/>.
/// </summary>
internal sealed class PythonRunner : IPythonRunner, IRunEngine, IDisposable
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> NoFiles = new Dictionary<string, IReadOnlyList<PsrFile>>();

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
    private readonly ConcurrentDictionary<RunId, CancellationTokenSource> _active = new();

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
        var stage = await ResolveStageAsync(stageKey, options?.Version, cancellationToken).ConfigureAwait(false);
        return await RunStageAsync(stage, RunKind.Stage, inputs, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunResult> RunPipelineAsync(
        string pipelineKey, object? inputs, RunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineKey);
        var pipeline = await ResolvePipelineAsync(pipelineKey, options?.Version, cancellationToken).ConfigureAwait(false);
        return await RunPipelineAsync(pipeline, RunKind.Pipeline, inputs, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunResult> RunStageAsync(
        StageDefinition stage, RunKind kind, object? inputs, RunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        var settings = Settings;
        _janitor.EnsureCleaned();
        var bound = InputBinder.Bind(stage.Inputs, inputs, settings.MaxFileBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var target = new RunTarget(kind, stage.Key, stage.Version, [new PlannedStep(stage.Key, 1, stage.Key, stage.Version)]);
        return await RunNewAsync(target, bound, options, settings,
            (context, inputFiles, token) => ExecuteStageAsync(stage, bound.Wire, inputFiles, context, prior: null, token),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunResult> RunPipelineAsync(
        PipelineDefinition pipeline, RunKind kind, object? inputs, RunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var settings = Settings;
        _janitor.EnsureCleaned();
        var bound = InputBinder.Bind(pipeline.Inputs, inputs, settings.MaxFileBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var steps = pipeline.Steps.Select((s, i) => new PlannedStep(s.Key, i + 1, s.StageKey, s.StageVersion)).ToList();
        var target = new RunTarget(kind, pipeline.Key, pipeline.Version, steps);
        return await RunNewAsync(target, bound, options, settings,
            (context, inputFiles, token) => ExecutePipelineAsync(pipeline, bound.Wire, inputFiles, context, NoPrevious, token),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunResult> ResumeAsync(ResumeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((request.Stage is null) == (request.Pipeline is null))
        {
            throw new ArgumentException("Set exactly one of Stage or Pipeline.", nameof(request));
        }

        var settings = Settings;
        _janitor.EnsureCleaned();
        var context = new RunContext(new RunWorkspace(settings.WorkRoot, request.RunId), settings, request.Kind, request.TriggeredBy);
        var started = _time.GetTimestamp();
        using var cancellation = Register(request.RunId, cancellationToken);
        var token = cancellation.Token;
        try
        {
            var inputFiles = await MaterializeStoredAsync(request.InputFiles, context.Workspace.InputsDirectory, token).ConfigureAwait(false);
            var previous = new Dictionary<string, StepResult>(StringComparer.Ordinal);
            foreach (var step in request.PreviousSteps)
            {
                previous[step.StepKey] = step.Status == StepStatus.Succeeded
                    ? step with
                    {
                        Files = await MaterializeStoredAsync(step.Files, Path.Combine(context.Workspace.ResultsDirectory, step.StepKey), token)
                            .ConfigureAwait(false),
                    }
                    : step;
            }

            context.UsedBytes = TotalBytes(inputFiles) + previous.Values.Sum(s => TotalBytes(s.Files));
            await _recorder.RunResumingAsync(new RunResuming(request.RunId, request.TriggeredBy, _time.GetUtcNow()), token).ConfigureAwait(false);
            return await CompleteAsync(context, started, () => request.Stage is { } stage
                ? ExecuteStageAsync(stage, request.Inputs, inputFiles, context, previous.GetValueOrDefault(stage.Key), token)
                : ExecutePipelineAsync(request.Pipeline!, request.Inputs, inputFiles, context, previous, token), token).ConfigureAwait(false);
        }
        catch
        {
            await context.Workspace.DeleteResultsAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _active.TryRemove(request.RunId, out _);
            context.Workspace.DeleteRun();
        }
    }

    public bool RequestCancellation(RunId runId)
    {
        if (!_active.TryGetValue(runId, out var source))
        {
            return false;
        }

        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_concurrency.IsValueCreated)
        {
            _concurrency.Value.Dispose();
        }
    }

    private static IReadOnlyDictionary<string, StepResult> NoPrevious { get; } = new Dictionary<string, StepResult>();

    private async Task<RunResult> RunNewAsync(
        RunTarget target,
        BoundInputs bound,
        RunOptions? options,
        PythonScriptRunnerOptions settings,
        Func<RunContext, IReadOnlyDictionary<string, IReadOnlyList<PsrFile>>, CancellationToken, Task<RunOutcome>> body,
        CancellationToken cancellationToken)
    {
        var runId = RunId.New(_time.GetUtcNow());
        var context = new RunContext(new RunWorkspace(settings.WorkRoot, runId), settings, target.Kind, options?.TriggeredBy ?? Actor.Library.Subject);
        var started = _time.GetTimestamp();
        using var cancellation = Register(runId, cancellationToken);
        var token = cancellation.Token;
        try
        {
            var inputFiles = await MaterializeInputsAsync(bound.Files, context.Workspace, token).ConfigureAwait(false);
            context.UsedBytes = TotalBytes(inputFiles);
            var overBudget = context.UsedBytes > settings.MaxRunFilesBytes;
            var existing = await _recorder.RunStartingAsync(
                new RunStarting
                {
                    RunId = runId,
                    Kind = target.Kind,
                    TargetKey = target.Key,
                    TargetVersion = target.Version,
                    Trigger = options?.Trigger ?? RunTrigger.Library,
                    TriggeredBy = context.TriggeredBy,
                    IdempotencyKey = options?.IdempotencyKey,
                    Inputs = (JsonObject)bound.Wire.DeepClone(),
                    InputFiles = overBudget ? NoFiles : inputFiles,
                    Steps = target.Steps,
                    StartedAt = runId.CreatedAt,
                },
                token).ConfigureAwait(false);
            if (existing is not null)
            {
                await context.Workspace.DeleteResultsAsync().ConfigureAwait(false);
                return existing;
            }

            return await CompleteAsync(context, started, () => overBudget
                ? Task.FromResult(RunOutcome.Aborted(RunStatus.Failed, new RunError(ErrorKind.ArtifactLimitExceeded, "The input files exceed the run limit.")
                {
                    Code = "RUN_FILES_TOO_LARGE",
                }))
                : body(context, inputFiles, token), token).ConfigureAwait(false);
        }
        catch
        {
            await context.Workspace.DeleteResultsAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _active.TryRemove(runId, out _);
            context.Workspace.DeleteRun();
        }
    }

    /// <summary>Executes the body, records the end of the run and builds the result.</summary>
    private async Task<RunResult> CompleteAsync(RunContext context, long started, Func<Task<RunOutcome>> body, CancellationToken cancellationToken)
    {
        RunOutcome outcome;
        try
        {
            outcome = await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = RunOutcome.Aborted(RunStatus.Cancelled, new RunError(ErrorKind.Cancelled, "The run was cancelled."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordCrashAsync(context.RunId, ex).ConfigureAwait(false);
            throw;
        }

        await _recorder.RunFinishedAsync(
            new RunFinished(context.RunId, outcome.Status, _time.GetUtcNow(), outcome.Outputs, outcome.Files, outcome.Error),
            CancellationToken.None).ConfigureAwait(false);
        return new RunResult(context.RunId, context.Kind, outcome.Status, _time.GetElapsedTime(started), outcome.Steps, outcome.Outputs, outcome.Files,
            outcome.Error, context.Workspace.DeleteResultsAsync);
    }

    /// <summary>Best effort: a run interrupted by an unexpected error must not stay "running" in the history.</summary>
    private async Task RecordCrashAsync(RunId runId, Exception exception)
    {
        try
        {
            var error = new RunError(ErrorKind.ProcessCrashed, $"The runner failed: {exception.Message}") { Code = "RUNNER_ERROR" };
            await _recorder.RunFinishedAsync(new RunFinished(runId, RunStatus.Failed, _time.GetUtcNow(), null, null, error), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception recordError) when (recordError is not OutOfMemoryException)
        {
            Log.RecordingFailed(_logger, runId, recordError);
        }
    }

    private CancellationTokenSource Register(RunId runId, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _active[runId] = source;
        return source;
    }

    private async Task<RunOutcome> ExecuteStageAsync(
        StageDefinition stage,
        JsonObject inputs,
        IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> files,
        RunContext context,
        StepResult? prior,
        CancellationToken cancellationToken)
    {
        var step = await ExecuteStepAsync(stage, stage.Key, 1, inputs, files, context, prior, cancellationToken).ConfigureAwait(false);
        return new RunOutcome(StatusOf(step.Status), [step], step.Outputs, step.Files, step.Error);
    }

    private async Task<RunOutcome> ExecutePipelineAsync(
        PipelineDefinition pipeline,
        JsonObject pipelineInputs,
        IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> pipelineFiles,
        RunContext context,
        IReadOnlyDictionary<string, StepResult> previous,
        CancellationToken cancellationToken)
    {
        var steps = new List<StepResult>();
        var completed = new Dictionary<string, StepResult>(StringComparer.Ordinal);
        var status = RunStatus.Succeeded;
        RunError? error = null;
        for (var i = 0; i < pipeline.Steps.Count; i++)
        {
            var definition = pipeline.Steps[i];
            var prior = previous.GetValueOrDefault(definition.Key);
            if (status != RunStatus.Succeeded)
            {
                steps.Add(await RecordStepAsync(context, Skipped(definition, i + 1, prior)).ConfigureAwait(false));
                continue;
            }

            if (prior is { Status: StepStatus.Succeeded })
            {
                steps.Add(prior);
                completed[definition.Key] = prior;
                continue;
            }

            var stage = await _catalog.FindStageAsync(definition.StageKey, definition.StageVersion, cancellationToken).ConfigureAwait(false)
                ?? throw new PsrConfigurationException($"Stage '{definition.StageKey}@{definition.StageVersion}' of pipeline '{pipeline.Key}' is not in the catalog.");
            var inputs = StepInputResolver.Resolve(stage, pipeline, i, pipelineInputs, pipelineFiles, completed);
            var result = inputs.Errors.Count > 0
                ? await RecordStepAsync(context, InvalidInputs(stage, definition.Key, i + 1, inputs.Errors, prior)).ConfigureAwait(false)
                : await ExecuteStepAsync(stage, definition.Key, i + 1, inputs.Wire, inputs.Files, context, prior, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Runs the attempts of one step; a step resumed after a failure continues the attempt numbering.</summary>
    private async Task<StepResult> ExecuteStepAsync(
        StageDefinition stage,
        string stepKey,
        int order,
        JsonObject inputs,
        IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> files,
        RunContext context,
        StepResult? prior,
        CancellationToken cancellationToken)
    {
        await _recorder.StepStartedAsync(
            new StepStarted(context.RunId, stepKey, order, WithFilePlaceholders(inputs, files, stage), files, _time.GetUtcNow()),
            CancellationToken.None).ConfigureAwait(false);

        var attempts = new List<AttemptResult>(prior?.Attempts ?? []);
        var first = attempts.Count + 1;
        var firstCause = prior is { Status: not (StepStatus.Pending or StepStatus.Skipped) } ? AttemptCause.Reprocess : AttemptCause.Initial;
        var copies = ToCopies(files);
        StepResult Finish(StepStatus status, RunError? error, AttemptOutcome? success = null) => new()
        {
            StepKey = stepKey,
            Order = order,
            StageKey = stage.Key,
            StageVersion = stage.Version,
            Status = status,
            Attempts = attempts,
            Outputs = success?.Outputs,
            Files = success?.Files ?? NoFiles,
            Error = error is null ? null : error with { StepKey = stepKey },
        };

        StepResult result;
        for (var number = first; ; number++)
        {
            var cause = number == first ? firstCause : AttemptCause.AutoRetry;
            var outcome = await RunAttemptAsync(stage, stepKey, order, number, cause, inputs, copies, context, cancellationToken).ConfigureAwait(false);
            attempts.Add(outcome.Attempt);
            await _recorder.AttemptFinishedAsync(new AttemptFinished(context.RunId, stepKey, order, outcome.Attempt), CancellationToken.None)
                .ConfigureAwait(false);

            if (outcome.Attempt.Status == AttemptStatus.Succeeded)
            {
                context.UsedBytes += TotalBytes(outcome.Files);
                result = Finish(StepStatus.Succeeded, null, outcome);
                break;
            }

            if (outcome.Attempt.Status == AttemptStatus.Cancelled)
            {
                result = Finish(StepStatus.Cancelled, outcome.Attempt.Error);
                break;
            }

            var error = outcome.Attempt.Error!;
            Log.AttemptFailed(_logger, stage.Key, number, error.Kind, error.Message);
            if (number - first >= stage.Retry.MaxAttempts)
            {
                result = Finish(outcome.Attempt.Status == AttemptStatus.TimedOut ? StepStatus.TimedOut : StepStatus.Failed, error);
                break;
            }

            var delay = stage.Retry.GetDelay(number - first + 1);
            Log.RetryScheduled(_logger, stage.Key, delay, number + 1);
            try
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = Finish(StepStatus.Cancelled, new RunError(ErrorKind.Cancelled, "The run was cancelled while waiting to retry."));
                break;
            }
        }

        return await RecordStepAsync(context, result).ConfigureAwait(false);
    }

    private async Task<StepResult> RecordStepAsync(RunContext context, StepResult step)
    {
        await _recorder.StepFinishedAsync(new StepFinished(context.RunId, step, _time.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
        return step;
    }

    private async Task<AttemptOutcome> RunAttemptAsync(
        StageDefinition stage,
        string stepKey,
        int order,
        int number,
        AttemptCause cause,
        JsonObject inputs,
        IReadOnlyList<InputFileCopy> files,
        RunContext context,
        CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var attemptId = AttemptId.New();
        var startedAt = _time.GetUtcNow();
        var started = _time.GetTimestamp();
        var queued = TimeSpan.Zero;
        string? environmentHash = null;
        var workDirectory = context.Workspace.AttemptDirectory(stepKey, number);
        await _recorder.AttemptStartedAsync(
            new AttemptStarted(context.RunId, stepKey, order, attemptId, number, cause, context.TriggeredBy, startedAt),
            CancellationToken.None).ConfigureAwait(false);

        using var activity = PsrDiagnostics.Source.StartActivity("psr.stage.execute");
        activity?.SetTag("psr.stage.key", stage.Key);
        activity?.SetTag("psr.stage.version", stage.Version);
        activity?.SetTag("psr.run.id", context.RunId.ToString());
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
                            RunId = context.RunId,
                            StepKey = stepKey,
                            Attempt = number,
                            IsTest = context.Kind is RunKind.StageTest or RunKind.PipelineTest,
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
                Id = attemptId,
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
                list.Add(await CopyToAsync(bound.File, Path.Combine(workspace.InputsDirectory, bound.Field, bound.Index.ToString(CultureInfo.InvariantCulture)),
                    cancellationToken).ConfigureAwait(false));
            }

            result[group.Key] = list;
        }

        return result;
    }

    /// <summary>Downloads stored files (e.g. from the database) to <paramref name="directory"/>; files already on disk are kept.</summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<PsrFile>>> MaterializeStoredAsync(
        IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> files, string directory, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<PsrFile>>(StringComparer.Ordinal);
        foreach (var (field, list) in files)
        {
            var local = new List<PsrFile>();
            for (var i = 0; i < list.Count; i++)
            {
                var file = list[i];
                local.Add(file.LocalPath is not null
                    ? file
                    : await CopyToAsync(file, Path.Combine(directory, field, i.ToString(CultureInfo.InvariantCulture)), cancellationToken).ConfigureAwait(false));
            }

            result[field] = local;
        }

        return result;
    }

    private static async Task<PsrFile> CopyToAsync(PsrFile file, string directory, CancellationToken cancellationToken)
    {
        var name = RunWorkspace.SafeFileName(file.FileName);
        var target = Path.Combine(directory, name);
        Directory.CreateDirectory(directory);
        await file.SaveAsAsync(target, cancellationToken).ConfigureAwait(false);

        string sha256;
        await using (var stream = File.OpenRead(target))
        {
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        var copy = PsrFile.FromProducedFile(target, name, file.ContentType, sha256);
        if (file.ArtifactId is { } artifactId)
        {
            copy.AttachArtifact(artifactId);
        }

        return copy;
    }

    /// <summary>Copy of the step inputs with <c>{"$file": {...}}</c> metadata for file fields (a list for <c>list&lt;file&gt;</c>), as recorded.</summary>
    private static JsonObject WithFilePlaceholders(JsonObject inputs, IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> files, StageDefinition stage)
    {
        var copy = (JsonObject)inputs.DeepClone();
        foreach (var (field, list) in files)
        {
            var references = list.Select(file => (JsonNode)new JsonObject
            {
                [WireValidator.FileMarker] = new JsonObject { ["name"] = file.FileName, ["contentType"] = file.ContentType },
            }).ToList();
            var isList = stage.Inputs.FirstOrDefault(f => f.Name == field)?.Type.Kind == FieldKind.List;
            copy[field] = isList ? new JsonArray([.. references]) : references.FirstOrDefault();
        }

        return copy;
    }

    private static long TotalBytes(IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> files) =>
        files.Values.SelectMany(list => list).Sum(file => file.Length ?? 0);

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

    private static StepResult Skipped(PipelineStepDefinition definition, int order, StepResult? prior) => new()
    {
        StepKey = definition.Key,
        Order = order,
        StageKey = definition.StageKey,
        StageVersion = definition.StageVersion,
        Status = StepStatus.Skipped,
        Attempts = prior?.Attempts ?? [],
    };

    private static StepResult InvalidInputs(StageDefinition stage, string stepKey, int order, IReadOnlyList<InputError> errors, StepResult? prior) => new()
    {
        StepKey = stepKey,
        Order = order,
        StageKey = stage.Key,
        StageVersion = stage.Version,
        Status = StepStatus.Failed,
        Attempts = prior?.Attempts ?? [],
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

    private sealed record RunTarget(RunKind Kind, string Key, int Version, IReadOnlyList<PlannedStep> Steps);

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

    /// <summary>State shared by the steps of one run (folders, settings, caller and the file budget).</summary>
    private sealed class RunContext(RunWorkspace workspace, PythonScriptRunnerOptions settings, RunKind kind, string triggeredBy)
    {
        public RunWorkspace Workspace { get; } = workspace;

        public RunId RunId => Workspace.RunId;

        public PythonScriptRunnerOptions Settings { get; } = settings;

        public RunKind Kind { get; } = kind;

        public string TriggeredBy { get; } = triggeredBy;

        public long UsedBytes { get; set; }
    }
}
