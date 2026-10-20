using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PythonScriptRunner.Dashboard.Tests.Infrastructure;

/// <summary>
/// Versioned JSON snapshots next to the tests (<c>Snapshots/&lt;name&gt;.verified.json</c>). A difference writes
/// <c>&lt;name&gt;.received.json</c> and fails; review it and replace the verified file, or run with <c>PSR_ACCEPT_SNAPSHOTS=1</c>.
/// </summary>
public static class Snapshot
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task MatchJsonAsync(string name, string json)
    {
        var folder = Path.Combine(RepositoryRoot(), "tests", "PythonScriptRunner.Dashboard.Tests", "Snapshots");
        var verified = Path.Combine(folder, $"{name}.verified.json");
        var received = Path.Combine(folder, $"{name}.received.json");
        var actual = (JsonNode.Parse(json) ?? throw new ArgumentException("Empty JSON.", nameof(json))).ToJsonString(Indented) + "\n";
        var expected = File.Exists(verified) ? (await File.ReadAllTextAsync(verified)).ReplaceLineEndings("\n") : null;
        if (actual == expected)
        {
            File.Delete(received);
            return;
        }

        Directory.CreateDirectory(folder);
        if (Environment.GetEnvironmentVariable("PSR_ACCEPT_SNAPSHOTS") == "1")
        {
            await File.WriteAllTextAsync(verified, actual);
            return;
        }

        await File.WriteAllTextAsync(received, actual);
        Assert.Fail(expected is null
            ? $"No snapshot {verified}; review {received} and rename it to .verified.json."
            : $"The snapshot changed: compare {received} with {verified} and replace the verified file when the change is intended."
              + Environment.NewLine + Differences(expected, actual));
    }

    /// <summary>The first lines that differ (the CI log shows them without the files).</summary>
    private static string Differences(string expected, string actual)
    {
        var before = expected.Split('\n');
        var after = actual.Split('\n');
        var lines = new List<string>();
        for (var i = 0; i < Math.Max(before.Length, after.Length) && lines.Count < 24; i++)
        {
            var left = i < before.Length ? before[i] : "<end>";
            var right = i < after.Length ? after[i] : "<end>";
            if (left != right)
            {
                lines.Add($"line {i + 1}:{Environment.NewLine}  - {left}{Environment.NewLine}  + {right}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The clone (deterministic CI builds map source paths to <c>/_/</c>, so the test file path cannot be used).</summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PythonScriptRunner.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"PythonScriptRunner.slnx not found above {AppContext.BaseDirectory}.");
    }
}
