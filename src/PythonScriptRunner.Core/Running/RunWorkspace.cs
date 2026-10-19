using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PythonScriptRunner.Core.Diagnostics;

namespace PythonScriptRunner.Core.Running;

/// <summary>Folders of one run: <c>runs/&lt;id&gt;</c> (work, deleted at the end) and <c>results/&lt;id&gt;</c> (kept until the result is disposed).</summary>
internal sealed class RunWorkspace(string workRoot, RunId runId)
{
    public RunId RunId { get; } = runId;

    public string RunDirectory { get; } = Path.Combine(workRoot, "runs", runId.ToString());

    public string ResultsDirectory { get; } = Path.Combine(workRoot, "results", runId.ToString());

    public string InputsDirectory => Path.Combine(RunDirectory, "inputs");

    public string AttemptDirectory(string stepKey, int attempt) =>
        Path.Combine(RunDirectory, stepKey, attempt.ToString(CultureInfo.InvariantCulture));

    public void DeleteRun() => TryDelete(RunDirectory);

    public ValueTask DeleteResultsAsync()
    {
        TryDelete(ResultsDirectory);
        return ValueTask.CompletedTask;
    }

    public static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: leftovers are removed by WorkspaceJanitor.
        }
    }

    /// <summary>A file name safe for every file system (no folders, no invalid characters).</summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. Path.GetFileName(name).Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c)]).Trim();
        if (cleaned.Length is 0 || cleaned is "." or "..")
        {
            return "file";
        }

        return cleaned.Length <= 120 ? cleaned : cleaned[..60] + cleaned[^60..];
    }
}

/// <summary>Removes work and result folders left behind by crashed processes (older than the retention).</summary>
internal sealed class WorkspaceJanitor(IOptions<PythonScriptRunnerOptions> options, TimeProvider time, ILogger<WorkspaceJanitor> logger)
{
    private int _cleaned;

    public void EnsureCleaned()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) == 1)
        {
            return;
        }

        var settings = options.Value;
        var cutoff = (time.GetUtcNow() - settings.OrphanRetention).UtcDateTime;
        var removed = 0;
        foreach (var area in (string[])["runs", "results"])
        {
            var folder = Path.Combine(settings.WorkRoot, area);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var child in Directory.EnumerateDirectories(folder))
            {
                if (Directory.GetLastWriteTimeUtc(child) < cutoff)
                {
                    RunWorkspace.TryDelete(child);
                    removed++;
                }
            }
        }

        if (removed > 0)
        {
            Log.OrphansRemoved(logger, removed, settings.OrphanRetention);
        }
    }
}
