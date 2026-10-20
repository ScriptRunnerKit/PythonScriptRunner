using System.Text;
using System.Text.Json.Nodes;
using PythonScriptRunner.EntityFrameworkCore.Catalog;
using PythonScriptRunner.EntityFrameworkCore.Entities;
using PythonScriptRunner.EntityFrameworkCore.Runs;

namespace PythonScriptRunner.EntityFrameworkCore;

/// <summary>
/// Writes the messages of run transitions (commands and events) to the outbox, in the transaction of the change. The default
/// writes nothing (no queue); the RabbitMQ package replaces it and its relay publishes the rows.
/// </summary>
public interface IRunMessageOutbox
{
    /// <summary>Whether messages are delivered (a queue is registered). Asynchronous runs require it.</summary>
    bool IsEnabled { get; }

    /// <summary>Asks a worker to execute one attempt of a step.</summary>
    /// <param name="db">Context of the change.</param>
    /// <param name="command">The command.</param>
    /// <param name="availableAt">Not delivered before this instant (retry delays).</param>
    /// <param name="traceParent">W3C trace context of the run.</param>
    void ExecuteStep(PsrDbContext db, ExecuteStepCommand command, DateTimeOffset availableAt, string? traceParent);

    /// <summary>Asks every instance to stop the processes of a run.</summary>
    /// <param name="db">Context of the change.</param>
    /// <param name="command">The command.</param>
    /// <param name="now">Current time.</param>
    void CancelRun(PsrDbContext db, CancelRunCommand command, DateTimeOffset now);

    /// <summary>Publishes a run event.</summary>
    /// <param name="db">Context of the change.</param>
    /// <param name="runEvent">The event.</param>
    /// <param name="traceParent">W3C trace context of the run.</param>
    void Publish(PsrDbContext db, RunEvent runEvent, string? traceParent);
}

/// <summary>No queue: nothing is written.</summary>
internal sealed class NullRunMessageOutbox : IRunMessageOutbox
{
    public bool IsEnabled => false;

    public void ExecuteStep(PsrDbContext db, ExecuteStepCommand command, DateTimeOffset availableAt, string? traceParent)
    {
    }

    public void CancelRun(PsrDbContext db, CancelRunCommand command, DateTimeOffset now)
    {
    }

    public void Publish(PsrDbContext db, RunEvent runEvent, string? traceParent)
    {
    }
}

/// <summary>Builds run events from persisted rows.</summary>
internal static class RunEventFactory
{
    /// <summary>Largest <c>outputs</c> carried by <c>run.succeeded</c> (QA-005-1).</summary>
    public const int MaxOutputBytes = 256 * 1024;

    public static RunEvent For(RunEntity run, string type, DateTimeOffset at) => new()
    {
        EventId = Guid.CreateVersion7(at),
        Type = type,
        OccurredAt = at,
        RunId = new RunId(run.Id),
        Kind = run.Kind,
        PipelineKey = run.PipelineKey,
        PipelineVersion = run.PipelineVersion,
        StageKey = run.StageKey,
        StageVersion = run.StageVersion,
        CorrelationId = run.CorrelationId,
        Trigger = run.Trigger,
        Status = run.Status,
        TriggerDetails = StoredJson.Object(run.TriggerDetails),
    };

    public static RunEvent ForStep(RunEntity run, StepRunEntity step, int attempt, string type, DateTimeOffset at) =>
        For(run, type, at) with { Step = new RunEventStep(step.StepKey, step.Order, attempt) };

    /// <summary><c>run.succeeded</c> with the outputs: file references become metadata; above 256 KB they are omitted.</summary>
    public static RunEvent Succeeded(RunEntity run, IReadOnlyDictionary<Guid, ArtifactEntity> artifacts, DateTimeOffset at)
    {
        var outputs = StoredJson.Object(run.Outputs) ?? [];
        foreach (var (name, value) in outputs.ToList())
        {
            outputs[name] = value switch
            {
                JsonArray array when array.All(IsReference) => new JsonArray([.. array.Select(item => Describe(item!, artifacts))]),
                JsonObject reference when IsReference(reference) => Describe(reference, artifacts),
                _ => value?.DeepClone(),
            };
        }

        var truncated = Encoding.UTF8.GetByteCount(outputs.ToJsonString()) > MaxOutputBytes;
        return For(run, RunEventTypes.RunSucceeded, at) with { Outputs = truncated ? null : outputs, OutputsTruncated = truncated };
    }

    private static bool IsReference(JsonNode? node) => node is JsonObject reference && reference[ArtifactRefs.Marker] is JsonValue;

    private static JsonNode Describe(JsonNode reference, IReadOnlyDictionary<Guid, ArtifactEntity> artifacts)
    {
        var id = Guid.Parse(reference[ArtifactRefs.Marker]!.GetValue<string>());
        var metadata = new JsonObject { ["id"] = id.ToString() };
        if (artifacts.TryGetValue(id, out var artifact))
        {
            metadata["name"] = artifact.FileName;
            metadata["contentType"] = artifact.ContentType;
            metadata["size"] = artifact.SizeBytes;
        }

        return new JsonObject { [ArtifactRefs.Marker] = metadata };
    }
}
