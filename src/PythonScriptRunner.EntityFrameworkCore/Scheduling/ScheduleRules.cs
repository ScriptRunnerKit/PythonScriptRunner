using System.Text.Json.Nodes;

namespace PythonScriptRunner.EntityFrameworkCore.Scheduling;

/// <summary>Rules shared by the schedule editor and the scheduler (D-77, D-89).</summary>
internal static class ScheduleRules
{
    /// <summary>The version a schedule runs: the pinned one or the latest published. <c>SCHEDULE_INVALID</c> when it cannot run.</summary>
    public static async Task<PipelineDefinition> ResolveAsync(ICatalog catalog, string pipelineKey, int? pinnedVersion, CancellationToken cancellationToken)
    {
        try
        {
            return await catalog.FindPipelineAsync(pipelineKey, pinnedVersion, cancellationToken).ConfigureAwait(false)
                ?? throw new PsrDomainException(PsrErrorCodes.ScheduleInvalid, pinnedVersion is { } version
                    ? $"Version {version} of pipeline '{pipelineKey}' does not exist."
                    : $"Pipeline '{pipelineKey}' has no published version.");
        }
        catch (VersionNotRunnableException ex)
        {
            throw new PsrDomainException(PsrErrorCodes.ScheduleInvalid, ex.Message, ex);
        }
    }

    /// <summary>Schedules cannot provide files: a required file input, or a file given as fixed input, is <c>SCHEDULE_FILE_INPUT</c>.</summary>
    public static void EnsureNoFileInputs(PipelineDefinition pipeline, JsonObject inputs)
    {
        var files = pipeline.Inputs.Where(f => f.Type.IsFile && (f.Required || inputs.ContainsKey(f.Name))).Select(f => f.Name).ToList();
        if (files.Count > 0)
        {
            throw new PsrDomainException(PsrErrorCodes.ScheduleFileInput,
                $"Pipeline '{pipeline.Key}@{pipeline.Version}' has file inputs ({string.Join(", ", files)}); schedules cannot provide files.");
        }
    }

    /// <summary>Required inputs of <paramref name="pipeline"/> that the fixed inputs do not provide (they have no default).</summary>
    public static IEnumerable<string> MissingInputs(PipelineDefinition pipeline, JsonObject inputs) =>
        pipeline.Inputs.Where(f => f.Required && f.Default is null && inputs[f.Name] is null).Select(f => f.Name);
}
