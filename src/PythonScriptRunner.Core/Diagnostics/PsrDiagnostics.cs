using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace PythonScriptRunner.Core.Diagnostics;

/// <summary>Tracing source shared by every PythonScriptRunner package.</summary>
internal static class PsrDiagnostics
{
    public const string Name = "PythonScriptRunner";

    public static readonly ActivitySource Source = new(Name);
}

/// <summary>Metrics of the runner (meter <c>PythonScriptRunner</c>).</summary>
internal sealed class PsrMetrics
{
    private readonly Histogram<double> _stepDuration;
    private readonly Histogram<double> _stepOverhead;
    private readonly Counter<long> _stepFailures;
    private readonly Counter<long> _environmentsCreated;

    public PsrMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(PsrDiagnostics.Name);
        _stepDuration = meter.CreateHistogram<double>("psr.step.duration", "ms", "Duration of each attempt.");
        _stepOverhead = meter.CreateHistogram<double>("psr.step.overhead", "ms", "Time spent by the runner outside the Python process.");
        _stepFailures = meter.CreateCounter<long>("psr.step.failures", description: "Failed attempts.");
        _environmentsCreated = meter.CreateCounter<long>("psr.environment.created", description: "Python environments created.");
    }

    public void AttemptFinished(string stageKey, AttemptResult attempt)
    {
        var stage = new KeyValuePair<string, object?>("psr.stage.key", stageKey);
        _stepDuration.Record(attempt.Duration.TotalMilliseconds, stage, new("psr.outcome", attempt.Status.ToString()));
        _stepOverhead.Record(attempt.Overhead.TotalMilliseconds, stage);
        if (attempt.Status != AttemptStatus.Succeeded)
        {
            _stepFailures.Add(1, stage, new("psr.error.kind", attempt.Error?.Kind.ToString()));
        }
    }

    public void EnvironmentCreated(string pythonVersion) =>
        _environmentsCreated.Add(1, new KeyValuePair<string, object?>("psr.python.version", pythonVersion));
}

/// <summary>Source-generated log messages.</summary>
internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Information, "Creating Python {PythonVersion} environment {EnvironmentHash} for stage {StageKey}")]
    public static partial void CreatingEnvironment(ILogger logger, string environmentHash, string pythonVersion, string stageKey);

    [LoggerMessage(2, LogLevel.Information, "Python environment {EnvironmentHash} created in {ElapsedMs:F0} ms")]
    public static partial void EnvironmentCreated(ILogger logger, string environmentHash, double elapsedMs);

    [LoggerMessage(3, LogLevel.Warning, "Stage {StageKey}@{StageVersion} has no requirements.lock; dependencies were locked now and cached. Commit a requirements.lock for reproducible environments.")]
    public static partial void GeneratingLock(ILogger logger, string stageKey, int stageVersion);

    [LoggerMessage(4, LogLevel.Warning, "Stage {StageKey} attempt {Attempt} failed: [{ErrorKind}] {Message}")]
    public static partial void AttemptFailed(ILogger logger, string stageKey, int attempt, ErrorKind errorKind, string message);

    [LoggerMessage(5, LogLevel.Information, "Stage {StageKey} will retry in {Delay} (attempt {NextAttempt})")]
    public static partial void RetryScheduled(ILogger logger, string stageKey, TimeSpan delay, int nextAttempt);

    [LoggerMessage(6, LogLevel.Information, "Removed {Count} leftover work folders older than {Retention}")]
    public static partial void OrphansRemoved(ILogger logger, int count, TimeSpan retention);

    [LoggerMessage(7, LogLevel.Debug, "stdout of stage {StageKey} attempt {Attempt}:\n{Output}")]
    public static partial void ScriptStdout(ILogger logger, string stageKey, int attempt, string output);

    [LoggerMessage(8, LogLevel.Information, "stderr of stage {StageKey} attempt {Attempt}:\n{Output}")]
    public static partial void ScriptStderr(ILogger logger, string stageKey, int attempt, string output);
}
