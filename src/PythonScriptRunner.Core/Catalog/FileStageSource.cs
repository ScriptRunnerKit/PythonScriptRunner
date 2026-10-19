using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace PythonScriptRunner.Core.Catalog;

/// <summary>Loads stages from <c>&lt;root&gt;/stages/&lt;folder&gt;/stage.json</c> (+ script and requirements).</summary>
internal sealed class FileStageSource(string root) : IStageSource
{
    private const string LockFileName = "requirements.lock";

    private static readonly Lazy<JsonSchema> Schema = new(LoadSchema, LazyThreadSafetyMode.ExecutionAndPublication);

    public string Description => $"folder '{root}'";

    public IReadOnlyList<StageDefinition> LoadStages()
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new PsrConfigurationException($"Catalog folder not found: {fullRoot}");
        }

        var stagesFolder = Path.Combine(fullRoot, "stages");
        if (!Directory.Exists(stagesFolder))
        {
            return [];
        }

        var errors = new List<string>();
        var stages = new List<StageDefinition>();
        foreach (var folder in Directory.EnumerateDirectories(stagesFolder).Order(StringComparer.Ordinal))
        {
            var manifest = Path.Combine(folder, "stage.json");
            if (!File.Exists(manifest))
            {
                errors.Add($"{folder}: stage.json not found");
                continue;
            }

            try
            {
                if (Load(manifest, errors) is { } stage)
                {
                    stages.Add(stage);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or ArgumentException or InvalidOperationException)
            {
                errors.Add($"{manifest}: {ex.Message}");
            }
        }

        return errors.Count > 0
            ? throw new PsrConfigurationException(
                "Invalid stage catalog:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)))
            : stages;
    }

    private static StageDefinition? Load(string manifest, List<string> errors)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(manifest),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var results = Schema.Value.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!results.IsValid)
        {
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
                errors.Add($"{manifest}: does not match schemas/stage.v1.json");
            }

            return null;
        }

        var json = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
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

    private static JsonSchema LoadSchema()
    {
        using var stream = typeof(FileStageSource).Assembly.GetManifestResourceStream("PythonScriptRunner.Schemas.stage.v1.json")
            ?? throw new InvalidOperationException("Embedded stage schema not found.");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }
}
