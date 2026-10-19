using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using PythonScriptRunner.Core.Pipelines;

namespace PythonScriptRunner.Core.Catalog;

/// <summary>
/// Loads stages from <c>&lt;root&gt;/stages/&lt;folder&gt;/stage.json</c> (+ script and requirements)
/// and pipelines from <c>&lt;root&gt;/pipelines/*.pipeline.json</c>.
/// </summary>
internal sealed class FileCatalogSource(string root) : ICatalogSource
{
    private const string LockFileName = "requirements.lock";

    private static readonly Lazy<JsonSchema> StageSchema = new(() => LoadSchema("stage.v1.json"), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<JsonSchema> PipelineSchema = new(() => LoadSchema("pipeline.v1.json"), LazyThreadSafetyMode.ExecutionAndPublication);

    public string Description => $"folder '{root}'";

    public IReadOnlyList<StageDefinition> LoadStages()
    {
        var folder = Subfolder("stages");
        if (folder is null)
        {
            return [];
        }

        var manifests = Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal).Select(d => Path.Combine(d, "stage.json"));
        return LoadAll(manifests, "stage catalog", (manifest, errors) =>
            File.Exists(manifest) ? LoadStage(manifest, errors) : Missing(manifest, errors));
    }

    public IReadOnlyList<PipelineDefinition> LoadPipelines()
    {
        var folder = Subfolder("pipelines");
        if (folder is null)
        {
            return [];
        }

        var manifests = Directory.EnumerateFiles(folder, "*.pipeline.json").Order(StringComparer.Ordinal);
        return LoadAll(manifests, "pipeline catalog", LoadPipeline);
    }

    private string? Subfolder(string name)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new PsrConfigurationException($"Catalog folder not found: {fullRoot}");
        }

        var folder = Path.Combine(fullRoot, name);
        return Directory.Exists(folder) ? folder : null;
    }

    private static List<T> LoadAll<T>(IEnumerable<string> manifests, string what, Func<string, List<string>, T?> load)
        where T : class
    {
        var errors = new List<string>();
        var items = new List<T>();
        foreach (var manifest in manifests)
        {
            try
            {
                if (load(manifest, errors) is { } item)
                {
                    items.Add(item);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or ArgumentException or InvalidOperationException or PsrConfigurationException)
            {
                errors.Add($"{manifest}: {ex.Message}");
            }
        }

        return errors.Count > 0
            ? throw new PsrConfigurationException($"Invalid {what}:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)))
            : items;
    }

    private static StageDefinition? Missing(string manifest, List<string> errors)
    {
        errors.Add($"{manifest}: stage.json not found");
        return null;
    }

    private static StageDefinition? LoadStage(string manifest, List<string> errors)
    {
        if (ParseAndValidate(manifest, StageSchema.Value, "schemas/stage.v1.json", errors) is not { } json)
        {
            return null;
        }

        var folder = Path.GetDirectoryName(manifest)!;
        var requirementsName = json["requirements"]?.GetValue<string>();
        var requirementsPath = Path.Combine(folder, requirementsName ?? "requirements.txt");
        string? requirements = null;
        if (File.Exists(requirementsPath))
        {
            requirements = File.ReadAllText(requirementsPath);
        }
        else if (requirementsName is not null)
        {
            errors.Add($"{manifest}: requirements file not found: {requirementsPath}");
            return null;
        }

        var lockPath = Path.Combine(folder, LockFileName);
        var retry = json["retry"]?.AsObject();
        return new StageDefinition
        {
            Key = json["key"]!.GetValue<string>(),
            Version = json["version"]!.GetValue<int>(),
            Name = json["name"]!.GetValue<string>(),
            Description = json["description"]?.GetValue<string>(),
            Script = ScriptSource.FromFile(Path.Combine(folder, json["script"]?.GetValue<string>() ?? "script.py")),
            PythonVersion = json["python"]?.GetValue<string>(),
            Requirements = requirements,
            LockedRequirements = File.Exists(lockPath) ? File.ReadAllText(lockPath) : null,
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

    private static PipelineDefinition? LoadPipeline(string manifest, List<string> errors) =>
        ParseAndValidate(manifest, PipelineSchema.Value, "schemas/pipeline.v1.json", errors) is { } json
            ? PipelineJson.FromJson(json)
            : null;

    private static JsonObject? ParseAndValidate(string manifest, JsonSchema schema, string schemaName, List<string> errors)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(manifest),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var results = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
        {
            return JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
        }

        var before = errors.Count;
        foreach (var detail in (IEnumerable<EvaluationResults>)[results, .. results.Details ?? []])
        {
            foreach (var message in detail.Errors?.Values ?? Enumerable.Empty<string>())
            {
                errors.Add($"{manifest}: {(detail.InstanceLocation.ToString() is { Length: > 0 } at ? at : "/")}: {message}");
            }
        }

        if (errors.Count == before)
        {
            errors.Add($"{manifest}: does not match {schemaName}");
        }

        return null;
    }

    private static JsonSchema LoadSchema(string name)
    {
        using var stream = typeof(FileCatalogSource).Assembly.GetManifestResourceStream($"PythonScriptRunner.Schemas.{name}")
            ?? throw new InvalidOperationException($"Embedded schema {name} not found.");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }
}
