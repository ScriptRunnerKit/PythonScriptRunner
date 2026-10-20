using System.Runtime.CompilerServices;
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

    public static async Task MatchJsonAsync(string name, string json, [CallerFilePath] string testFile = "")
    {
        var folder = Path.Combine(Path.GetDirectoryName(testFile)!, "Snapshots");
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
            : $"The snapshot changed: compare {received} with {verified} and replace the verified file when the change is intended.");
    }
}
