using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using PythonScriptRunner.Core.Pipelines;

namespace PythonScriptRunner;

/// <summary>
/// Reads and writes the folder catalog format: <c>stages/&lt;folder&gt;/stage.json</c> (+ <c>script.py</c>,
/// <c>requirements.txt</c>, <c>requirements.lock</c>) and <c>pipelines/&lt;key&gt;.pipeline.json</c>. Used by the folder
/// catalog and by import/export.
/// </summary>
public static class CatalogManifests
{
    /// <summary>Manifest file of a stage folder.</summary>
    public const string StageManifestName = "stage.json";

    /// <summary>Default script file of a stage folder.</summary>
    public const string ScriptName = "script.py";

    /// <summary>Default requirements file of a stage folder.</summary>
    public const string RequirementsName = "requirements.txt";

    /// <summary>Locked requirements of a stage folder (used as-is when present).</summary>
    public const string LockName = "requirements.lock";

    /// <summary>Suffix of pipeline manifests.</summary>
    public const string PipelineSuffix = ".pipeline.json";

    private const string SchemaBase = "https://raw.githubusercontent.com/ScriptRunnerKit/PythonScriptRunner/main/schemas/";

    private static readonly Lazy<JsonSchema> StageSchema = new(() => LoadSchema("stage.v1.json"), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<JsonSchema> PipelineSchema = new(() => LoadSchema("pipeline.v1.json"), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Writes the <c>stage.json</c> of a stage (script in <c>script.py</c>, requirements in <c>requirements.txt</c>).</summary>
    /// <param name="stage">The stage.</param>
    public static JsonObject WriteStage(StageDefinition stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        var json = new JsonObject
        {
            ["$schema"] = SchemaBase + "stage.v1.json",
            ["key"] = stage.Key,
            ["version"] = stage.Version,
            ["name"] = stage.Name,
        };
        if (stage.Description is not null)
        {
            json["description"] = stage.Description;
        }

        if (stage.PythonVersion is not null)
        {
            json["python"] = stage.PythonVersion;
        }

        json["script"] = ScriptName;
        if (!string.IsNullOrWhiteSpace(stage.Requirements))
        {
            json["requirements"] = RequirementsName;
        }

        if (stage.Timeout is { } timeout)
        {
            json["timeoutSeconds"] = (int)Math.Ceiling(timeout.TotalSeconds);
        }

        if (stage.MemoryLimitMb is { } memory)
        {
            json["memoryLimitMb"] = memory;
        }

        json["retry"] = new JsonObject
        {
            ["maxAttempts"] = stage.Retry.MaxAttempts,
            ["initialDelaySeconds"] = (int)stage.Retry.InitialDelay.TotalSeconds,
            ["backoffMultiplier"] = stage.Retry.BackoffMultiplier,
        };
        json["idempotent"] = stage.Idempotent;
        json["secrets"] = new JsonArray([.. stage.RequiredSecrets.Select(s => (JsonNode)JsonValue.Create(s))]);
        json["inputs"] = FieldDefinitionJson.ToJson(stage.Inputs);
        json["outputs"] = FieldDefinitionJson.ToJson(stage.Outputs);
        return json;
    }

    /// <summary>Writes the <c>*.pipeline.json</c> of a pipeline (compact binding syntax).</summary>
    /// <param name="pipeline">The pipeline.</param>
    public static JsonObject WritePipeline(PipelineDefinition pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var json = new JsonObject
        {
            ["$schema"] = SchemaBase + "pipeline.v1.json",
            ["key"] = pipeline.Key,
            ["version"] = pipeline.Version,
            ["name"] = pipeline.Name,
        };
        if (pipeline.Description is not null)
        {
            json["description"] = pipeline.Description;
        }

        json["inputs"] = FieldDefinitionJson.ToJson(pipeline.Inputs);
        json["steps"] = new JsonArray([.. pipeline.Steps.Select(step =>
        {
            var node = new JsonObject
            {
                ["key"] = step.Key,
                ["stage"] = $"{step.StageKey}@{step.StageVersion.ToString(CultureInfo.InvariantCulture)}",
            };
            if (step.DisplayName is not null)
            {
                node["displayName"] = step.DisplayName;
            }

            if (step.Bindings.Count > 0)
            {
                node["bindings"] = new JsonObject(step.Bindings.Select(b => KeyValuePair.Create(b.Input, (JsonNode?)WriteSource(b.Source))));
            }

            return (JsonNode)node;
        })]);
        json["outputs"] = new JsonArray([.. pipeline.Outputs.Select(output =>
        {
            var node = new JsonObject { ["name"] = output.Name, ["source"] = output.Source.ToString() };
            if (output.Description is not null)
            {
                node["description"] = output.Description;
            }

            return (JsonNode)node;
        })]);
        return json;
    }

    /// <summary>Writes field declarations in the manifest format (camelCase; see the data mapping).</summary>
    /// <param name="fields">Fields.</param>
    public static JsonArray WriteFields(IEnumerable<FieldDefinition> fields) => FieldDefinitionJson.ToJson(fields);

    /// <summary>Reads field declarations written by <see cref="WriteFields"/>.</summary>
    /// <param name="fields">JSON array of fields.</param>
    /// <exception cref="ArgumentException">A field is malformed.</exception>
    public static IReadOnlyList<FieldDefinition> ReadFields(JsonArray fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return [.. fields.Select(f => FieldDefinitionJson.FromJson(f!.AsObject()))];
    }

    /// <summary>Reads a <c>stage.json</c> validated against <c>schemas/stage.v1.json</c>.</summary>
    /// <param name="manifest">Content of <c>stage.json</c>.</param>
    /// <param name="script">The script of the stage.</param>
    /// <param name="requirements">Content of the requirements file, if any.</param>
    /// <param name="lockedRequirements">Content of <c>requirements.lock</c>, if any.</param>
    /// <exception cref="PsrConfigurationException">The manifest is not valid JSON or does not match the schema.</exception>
    public static StageDefinition ReadStage(string manifest, ScriptSource script, string? requirements, string? lockedRequirements)
    {
        var json = ParseValidated(manifest, StageSchema.Value, "schemas/stage.v1.json");
        return StageFromJson(json, script, requirements, lockedRequirements);
    }

    /// <summary>Reads a <c>*.pipeline.json</c> validated against <c>schemas/pipeline.v1.json</c>.</summary>
    /// <param name="manifest">Content of the manifest.</param>
    /// <exception cref="PsrConfigurationException">The manifest is not valid JSON or does not match the schema.</exception>
    public static PipelineDefinition ReadPipeline(string manifest)
    {
        var json = ParseValidated(manifest, PipelineSchema.Value, "schemas/pipeline.v1.json");
        try
        {
            return PipelineJson.FromJson(json);
        }
        catch (ArgumentException ex)
        {
            throw new PsrConfigurationException(ex.Message, ex);
        }
    }

    /// <summary>Builds a stage from a manifest already validated by the schema.</summary>
    internal static StageDefinition StageFromJson(JsonObject json, ScriptSource script, string? requirements, string? lockedRequirements)
    {
        var retry = json["retry"]?.AsObject();
        return new StageDefinition
        {
            Key = json["key"]!.GetValue<string>(),
            Version = json["version"]!.GetValue<int>(),
            Name = json["name"]!.GetValue<string>(),
            Description = json["description"]?.GetValue<string>(),
            Script = script,
            PythonVersion = json["python"]?.GetValue<string>(),
            Requirements = requirements,
            LockedRequirements = lockedRequirements,
            Inputs = [.. json["inputs"]!.AsArray().Select(f => FieldDefinitionJson.FromJson(f!.AsObject()))],
            Outputs = [.. json["outputs"]!.AsArray().Select(f => FieldDefinitionJson.FromJson(f!.AsObject()))],
            RequiredSecrets = json["secrets"] is JsonArray secrets ? [.. secrets.Select(s => s!.GetValue<string>())] : [],
            Timeout = json["timeoutSeconds"] is { } seconds ? TimeSpan.FromSeconds(seconds.GetValue<int>()) : null,
            MemoryLimitMb = json["memoryLimitMb"]?.GetValue<int>(),
            Retry = retry is null
                ? RetryPolicy.None
                : new RetryPolicy(
                    retry["maxAttempts"]?.GetValue<int>() ?? 0,
                    TimeSpan.FromSeconds(retry["initialDelaySeconds"]?.GetValue<int>() ?? 10),
                    retry["backoffMultiplier"]?.GetValue<double>() ?? 2.0),
            Idempotent = json["idempotent"]?.GetValue<bool>() ?? false,
        };
    }

    /// <summary>Parses a manifest and validates it against the schema; errors cite the JSON location.</summary>
    internal static JsonObject ParseValidated(string manifest, bool pipeline) =>
        ParseValidated(manifest, pipeline ? PipelineSchema.Value : StageSchema.Value, pipeline ? "schemas/pipeline.v1.json" : "schemas/stage.v1.json");

    private static JsonObject ParseValidated(string manifest, JsonSchema schema, string schemaName)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(manifest, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new PsrConfigurationException($"invalid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var results = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (results.IsValid)
            {
                return JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
            }

            var errors = new List<string>();
            foreach (var detail in (IEnumerable<EvaluationResults>)[results, .. results.Details ?? []])
            {
                foreach (var message in detail.Errors?.Values ?? Enumerable.Empty<string>())
                {
                    errors.Add($"{(detail.InstanceLocation.ToString() is { Length: > 0 } at ? at : "/")}: {message}");
                }
            }

            throw new PsrConfigurationException(errors.Count > 0 ? string.Join("; ", errors) : $"does not match {schemaName}");
        }
    }

    private static JsonNode WriteSource(BindingSource source) => source switch
    {
        ConstantSource constant => new JsonObject { ["const"] = constant.Value?.DeepClone() },
        _ => JsonValue.Create(source.ToString())!,
    };

    private static JsonSchema LoadSchema(string name)
    {
        using var stream = typeof(CatalogManifests).Assembly.GetManifestResourceStream($"PythonScriptRunner.Schemas.{name}")
            ?? throw new InvalidOperationException($"Embedded schema {name} not found.");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }
}
