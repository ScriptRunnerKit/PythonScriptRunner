using System.Text.Json;

namespace PythonScriptRunner.Core.Catalog;

/// <summary>
/// Loads stages from <c>&lt;root&gt;/stages/&lt;folder&gt;/stage.json</c> (+ script and requirements)
/// and pipelines from <c>&lt;root&gt;/pipelines/*.pipeline.json</c>.
/// </summary>
internal sealed class FileCatalogSource(string root) : ICatalogSource
{
    public string Description => $"folder '{root}'";

    public IReadOnlyList<StageDefinition> LoadStages()
    {
        var folder = Subfolder("stages");
        if (folder is null)
        {
            return [];
        }

        var manifests = Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal).Select(d => Path.Combine(d, CatalogManifests.StageManifestName));
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

        var manifests = Directory.EnumerateFiles(folder, "*" + CatalogManifests.PipelineSuffix).Order(StringComparer.Ordinal);
        return LoadAll(manifests, "pipeline catalog", (manifest, _) => CatalogManifests.ReadPipeline(File.ReadAllText(manifest)));
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
        var json = CatalogManifests.ParseValidated(File.ReadAllText(manifest), pipeline: false);
        var folder = Path.GetDirectoryName(manifest)!;
        var requirementsName = json["requirements"]?.GetValue<string>();
        var requirementsPath = Path.Combine(folder, requirementsName ?? CatalogManifests.RequirementsName);
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

        var lockPath = Path.Combine(folder, CatalogManifests.LockName);
        var script = ScriptSource.FromFile(Path.Combine(folder, json["script"]?.GetValue<string>() ?? CatalogManifests.ScriptName));
        return CatalogManifests.StageFromJson(json, script, requirements, File.Exists(lockPath) ? File.ReadAllText(lockPath) : null);
    }
}
