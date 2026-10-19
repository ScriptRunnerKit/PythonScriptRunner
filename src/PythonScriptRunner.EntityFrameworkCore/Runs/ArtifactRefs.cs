using System.Text.Json.Nodes;
using PythonScriptRunner.EntityFrameworkCore.Entities;

namespace PythonScriptRunner.EntityFrameworkCore.Runs;

/// <summary>
/// Converts file values between the wire format (<c>{"$file": {name, contentType, ...}}</c>) and the stored format
/// (<c>{"$artifact": "&lt;id&gt;"}</c>, data mapping §5.1); a <c>list&lt;file&gt;</c> is an array of either.
/// </summary>
internal static class ArtifactRefs
{
    public const string Marker = "$artifact";
    private const string FileMarker = "$file";

    /// <summary>Replaces the file fields of <paramref name="wire"/> with references to the stored files.</summary>
    public static JsonObject ToStored(JsonObject wire, IReadOnlyDictionary<string, IReadOnlyList<PsrFile>>? files)
    {
        var stored = (JsonObject)wire.DeepClone();
        foreach (var (field, list) in files ?? new Dictionary<string, IReadOnlyList<PsrFile>>())
        {
            if (list.Any(f => f.ArtifactId is null))
            {
                continue;
            }

            var references = list.Select(f => (JsonNode)new JsonObject { [Marker] = f.ArtifactId!.Value.ToString() }).ToList();
            stored[field] = stored[field] is JsonArray ? new JsonArray([.. references]) : references.FirstOrDefault();
        }

        return stored;
    }

    /// <summary>Artifact identifiers referenced by a stored object.</summary>
    public static IEnumerable<Guid> Referenced(JsonObject? stored) =>
        stored is null
            ? []
            : stored.Select(p => p.Value).SelectMany(v => v is JsonArray array ? array.AsEnumerable() : new[] { v })
                .Select(v => v is JsonObject reference && reference[Marker] is JsonValue id ? Guid.Parse(id.GetValue<string>()) : (Guid?)null)
                .Where(id => id is not null).Select(id => id!.Value);

    /// <summary>Turns a stored object back into the wire format and the files it references (streamed on demand).</summary>
    public static (JsonObject Wire, Dictionary<string, IReadOnlyList<PsrFile>> Files) ToWire(
        JsonObject? stored, IReadOnlyDictionary<Guid, ArtifactEntity> artifacts, Func<ArtifactId, CancellationToken, Task<Stream>> open)
    {
        var wire = new JsonObject();
        var files = new Dictionary<string, IReadOnlyList<PsrFile>>(StringComparer.Ordinal);
        foreach (var (field, value) in stored ?? [])
        {
            var items = value is JsonArray array ? array.ToList() : [value];
            if (items.Count == 0 || items.Any(i => i is not JsonObject o || !o.ContainsKey(Marker)))
            {
                wire[field] = value?.DeepClone();
                continue;
            }

            var list = new List<PsrFile>();
            var metadata = new List<JsonNode>();
            foreach (var item in items)
            {
                var id = Guid.Parse(item![Marker]!.GetValue<string>());
                if (!artifacts.TryGetValue(id, out var row))
                {
                    continue;
                }

                var artifactId = new ArtifactId(row.Id);
                list.Add(PsrFile.FromArtifact(artifactId, row.FileName, row.ContentType, row.SizeBytes, row.Sha256, ct => open(artifactId, ct)));
                metadata.Add(new JsonObject
                {
                    [FileMarker] = new JsonObject { ["name"] = row.FileName, ["contentType"] = row.ContentType, ["size"] = row.SizeBytes, ["sha256"] = row.Sha256 },
                });
            }

            files[field] = list;
            wire[field] = value is JsonArray ? new JsonArray([.. metadata]) : metadata.FirstOrDefault();
        }

        return (wire, files);
    }
}
