using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using PythonScriptRunner.Dashboard.Api;

namespace PythonScriptRunner.Dashboard.Realtime;

/// <summary>
/// Real-time run updates (<c>{prefix}/hubs/runs</c>, Reader): clients subscribe to a run, a pipeline, a stage or everything and
/// receive <c>RunUpdated</c> and <c>StepUpdated</c>. A connection in several groups may receive an update more than once; every
/// message carries the full state, so repeats are harmless.
/// </summary>
internal sealed partial class RunsHub : Hub
{
    public const string AllGroup = "all";
    public const string RunUpdated = nameof(RunUpdated);
    public const string StepUpdated = nameof(StepUpdated);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KeyPattern();

    public static string RunGroup(RunId runId) => $"run:{runId}";

    public static string PipelineGroup(string key) => $"pipeline:{key}";

    public static string StageGroup(string key) => $"stage:{key}";

    public Task SubscribeRun(string runId) => Groups.AddToGroupAsync(Context.ConnectionId, RunGroup(ParseRun(runId)));

    public Task UnsubscribeRun(string runId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, RunGroup(ParseRun(runId)));

    public Task SubscribePipeline(string key) => Groups.AddToGroupAsync(Context.ConnectionId, PipelineGroup(ParseKey(key)));

    public Task UnsubscribePipeline(string key) => Groups.RemoveFromGroupAsync(Context.ConnectionId, PipelineGroup(ParseKey(key)));

    public Task SubscribeStage(string key) => Groups.AddToGroupAsync(Context.ConnectionId, StageGroup(ParseKey(key)));

    public Task UnsubscribeStage(string key) => Groups.RemoveFromGroupAsync(Context.ConnectionId, StageGroup(ParseKey(key)));

    public Task SubscribeAll() => Groups.AddToGroupAsync(Context.ConnectionId, AllGroup);

    public Task UnsubscribeAll() => Groups.RemoveFromGroupAsync(Context.ConnectionId, AllGroup);

    private static RunId ParseRun(string runId) => RunId.TryParse(runId, out var id) ? id : throw new HubException("Invalid run id.");

    private static string ParseKey(string key) =>
        key is { Length: > 0 and <= 64 } && KeyPattern().IsMatch(key) ? key : throw new HubException("Invalid key.");
}

/// <summary>
/// Pushes run events to the browsers of this instance (D-35, D-110): a per-instance subscription to <c>psr.events</c>, so every
/// instance receives every event and no backplane is needed.
/// </summary>
internal sealed class RunEventBridge(IHubContext<RunsHub> hub, IRunManager runs) : IRunEventHandler
{
    public async Task HandleAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        if (runEvent.RunId is not { } runId || await runs.GetAsync(runId, cancellationToken).ConfigureAwait(false) is not { } run)
        {
            return;
        }

        string[] groups =
        [
            RunsHub.RunGroup(runId),
            RunsHub.AllGroup,
            run.Kind is RunKind.Pipeline or RunKind.PipelineTest ? RunsHub.PipelineGroup(run.TargetKey) : RunsHub.StageGroup(run.TargetKey),
        ];
        var clients = hub.Clients.Groups(groups);
        var current = run.Steps.LastOrDefault(s => s.Status is not (StepStatus.Pending or StepStatus.Skipped)) ?? run.Steps.FirstOrDefault();
        var summary = new RunSummary(run.Id, run.Kind, run.Mode, run.Status, run.TargetKey, run.TargetVersion, run.Trigger, run.TriggeredBy,
            run.CreatedAt, run.StartedAt, run.FinishedAt, run.Error);
        if (runEvent.Step is { } eventStep && run.Steps.FirstOrDefault(s => s.Order == eventStep.Order) is { } step)
        {
            await clients.SendAsync(RunsHub.StepUpdated, new StepUpdate(runId, step.StepKey, step.Order, step.Status, eventStep.Attempt, step.StartedAt,
                step.FinishedAt, step.Error?.Kind), cancellationToken).ConfigureAwait(false);
        }

        await clients.SendAsync(RunsHub.RunUpdated, new RunUpdate(summary, runEvent.Type, current?.StepKey), cancellationToken).ConfigureAwait(false);
    }
}
