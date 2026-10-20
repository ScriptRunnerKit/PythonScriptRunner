using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PythonScriptRunner;

/// <summary>
/// Internal command <c>psr.execute-step.v1</c>: execute one attempt of a step. It carries identifiers only; inputs and files
/// stay in the database.
/// </summary>
/// <param name="RunId">Run identifier.</param>
/// <param name="RunCreatedAt">Creation time of the run (its partition).</param>
/// <param name="StepRunId">Step identifier.</param>
/// <param name="Attempt">Attempt number to start.</param>
public sealed record ExecuteStepCommand(RunId RunId, DateTimeOffset RunCreatedAt, StepRunId StepRunId, int Attempt)
{
    /// <summary>Why the attempt starts.</summary>
    public AttemptCause Cause { get; init; } = AttemptCause.Initial;

    /// <summary>Who started the attempt (the reprocessing user, or the run's caller).</summary>
    public string? TriggeredBy { get; init; }
}

/// <summary>
/// Public command <c>psr.start-pipeline-run.v1</c> (exchange <c>psr.public</c>): lets systems that are not .NET start a
/// pipeline. Inputs cannot contain files.
/// </summary>
public sealed record StartPipelineRunCommand
{
    /// <summary>Pipeline key.</summary>
    public required string PipelineKey { get; init; }

    /// <summary>Version to run; <c>null</c> runs the latest published version.</summary>
    public int? Version { get; init; }

    /// <summary>Pipeline inputs (no files).</summary>
    public JsonObject? Inputs { get; init; }

    /// <summary>Deduplicates repeated commands for 24 hours (default: the AMQP <c>message_id</c>).</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Returned in every event of the run.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Name of the calling system (the run is triggered by <c>queue:&lt;name&gt;</c>).</summary>
    public string? RequestedBy { get; init; }
}

/// <summary>Control message <c>psr.cancel-run.v1</c> (fanout to every instance): stop the processes of a run.</summary>
/// <param name="RunId">Run identifier.</param>
/// <param name="StepRunId">Running step, when known.</param>
/// <param name="RequestedBy">Who cancelled.</param>
public sealed record CancelRunCommand(RunId RunId, StepRunId? StepRunId, string RequestedBy);

/// <summary>Versioned message types (AMQP <c>type</c> property).</summary>
public static class PsrMessageTypes
{
    /// <summary><see cref="ExecuteStepCommand"/>.</summary>
    public const string ExecuteStep = "psr.execute-step.v1";

    /// <summary><see cref="StartPipelineRunCommand"/>.</summary>
    public const string StartPipelineRun = "psr.start-pipeline-run.v1";

    /// <summary><see cref="CancelRunCommand"/>.</summary>
    public const string CancelRun = "psr.cancel-run.v1";

    /// <summary><see cref="RunEvent"/>.</summary>
    public const string RunEvent = "psr.run-event.v1";
}

/// <summary>JSON settings of the messages: camelCase properties and enum names as strings.</summary>
public static class PsrMessageJson
{
    /// <summary>Shared options (do not modify).</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>Writes <see cref="RunId"/> as its GUID text.</summary>
internal sealed class RunIdJsonConverter : JsonConverter<RunId>
{
    public override RunId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, RunId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}

/// <summary>Writes <see cref="StepRunId"/> as its GUID text.</summary>
internal sealed class StepRunIdJsonConverter : JsonConverter<StepRunId>
{
    public override StepRunId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, StepRunId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}
