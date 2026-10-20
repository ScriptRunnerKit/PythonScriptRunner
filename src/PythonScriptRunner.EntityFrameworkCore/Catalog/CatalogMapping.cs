using System.Security.Cryptography;
using System.Text;
using PythonScriptRunner.EntityFrameworkCore.Entities;

namespace PythonScriptRunner.EntityFrameworkCore.Catalog;

/// <summary>A step row with the stage version it references.</summary>
internal sealed record StepRow(PipelineStepEntity Step, string StageKey, int StageVersion);

/// <summary>Maps catalog rows to definitions and DTOs, and back.</summary>
internal static class CatalogMapping
{
    public static StageDefinition ToStage(StageEntity stage, StageVersionEntity version) => new()
    {
        Key = stage.Key,
        Version = version.Version,
        Name = stage.Name,
        Description = stage.Description,
        Script = ScriptSource.Inline(version.Script),
        PythonVersion = version.PythonVersion,
        Requirements = version.Requirements,
        LockedRequirements = version.LockedRequirements,
        Inputs = StoredJson.Fields(version.InputFields),
        Outputs = StoredJson.Fields(version.OutputFields),
        RequiredSecrets = version.RequiredSecrets,
        Timeout = TimeSpan.FromSeconds(version.TimeoutSeconds),
        MemoryLimitMb = version.MemoryLimitMb,
        Retry = new RetryPolicy(version.RetryMaxAttempts, TimeSpan.FromSeconds(version.RetryInitialDelaySeconds), (double)version.RetryBackoffMultiplier),
        Idempotent = version.Idempotent,
    };

    public static StageDraftData ToStageData(StageVersionEntity version) => new()
    {
        Script = version.Script,
        PythonVersion = version.PythonVersion,
        Requirements = version.Requirements,
        Inputs = StoredJson.Fields(version.InputFields),
        Outputs = StoredJson.Fields(version.OutputFields),
        RequiredSecrets = version.RequiredSecrets,
        Timeout = TimeSpan.FromSeconds(version.TimeoutSeconds),
        MemoryLimitMb = version.MemoryLimitMb,
        Retry = new RetryPolicy(version.RetryMaxAttempts, TimeSpan.FromSeconds(version.RetryInitialDelaySeconds), (double)version.RetryBackoffMultiplier),
        Idempotent = version.Idempotent,
    };

    /// <summary>Copies editable content into a version row.</summary>
    public static void Apply(StageVersionEntity version, StageDraftData data)
    {
        version.Script = data.Script;
        version.ScriptSha256 = Sha256(data.Script);
        version.PythonVersion = data.PythonVersion;
        version.Requirements = string.IsNullOrWhiteSpace(data.Requirements) ? null : data.Requirements;
        version.InputFields = StoredJson.Fields(data.Inputs);
        version.OutputFields = StoredJson.Fields(data.Outputs);
        version.RequiredSecrets = [.. data.RequiredSecrets];
        version.TimeoutSeconds = (int)Math.Ceiling(data.Timeout.TotalSeconds);
        version.MemoryLimitMb = data.MemoryLimitMb;
        version.RetryMaxAttempts = data.Retry.MaxAttempts;
        version.RetryInitialDelaySeconds = (int)data.Retry.InitialDelay.TotalSeconds;
        version.RetryBackoffMultiplier = (decimal)data.Retry.BackoffMultiplier;
        version.Idempotent = data.Idempotent;
    }

    public static StageDraftData ToStageData(StageDefinition stage, string defaultPythonVersion) => new()
    {
        Script = stage.Script switch
        {
            InlineScriptSource inline => inline.Content,
            FileScriptSource file => File.ReadAllText(file.Path),
            _ => throw new InvalidOperationException("Unknown script source."),
        },
        PythonVersion = stage.PythonVersion ?? defaultPythonVersion,
        Requirements = stage.Requirements,
        Inputs = stage.Inputs,
        Outputs = stage.Outputs,
        RequiredSecrets = stage.RequiredSecrets,
        Timeout = stage.Timeout ?? TimeSpan.FromMinutes(5),
        MemoryLimitMb = stage.MemoryLimitMb,
        Retry = stage.Retry,
        Idempotent = stage.Idempotent,
    };

    public static StageDraft ToStageDraft(StageEntity stage, StageVersionEntity version) =>
        new(stage.Key, version.Version, ToStageData(version), version.RowVersion, Authorship(version));

    public static StageVersionDetail ToStageVersionDetail(StageEntity stage, StageVersionEntity version) => new()
    {
        Key = stage.Key,
        Version = version.Version,
        Status = version.Status,
        Data = ToStageData(version),
        ScriptSha256 = version.ScriptSha256,
        LockedRequirements = version.LockedRequirements,
        EnvironmentHash = version.EnvironmentHash,
        PublishedAt = version.PublishedAt,
        PublishedBy = version.PublishedBy,
        ArchivedAt = version.ArchivedAt,
        ArchivedBy = version.ArchivedBy,
        RowVersion = version.RowVersion,
        Authorship = Authorship(version),
    };

    public static PipelineDefinition ToPipeline(PipelineEntity pipeline, PipelineVersionEntity version, IEnumerable<StepRow> steps) => new()
    {
        Key = pipeline.Key,
        Version = version.Version,
        Name = pipeline.Name,
        Description = pipeline.Description,
        Inputs = StoredJson.Fields(version.InputFields),
        Steps = [.. steps.OrderBy(s => s.Step.Order).Select(s => new PipelineStepDefinition(s.Step.Key, s.StageKey, s.StageVersion)
        {
            DisplayName = s.Step.DisplayName,
            Bindings = StoredJson.Bindings(s.Step.Bindings),
        })],
        Outputs = StoredJson.Outputs(version.Outputs),
    };

    public static PipelineDraftData ToPipelineData(PipelineDefinition pipeline, string? layout) => new()
    {
        Inputs = pipeline.Inputs,
        Steps = pipeline.Steps,
        Outputs = pipeline.Outputs,
        Layout = StoredJson.Node(layout),
    };

    public static PipelineVersionDetail ToPipelineVersionDetail(PipelineEntity pipeline, PipelineVersionEntity version, IEnumerable<StepRow> steps) => new()
    {
        Key = pipeline.Key,
        Version = version.Version,
        Status = version.Status,
        Data = ToPipelineData(ToPipeline(pipeline, version, steps), version.Layout),
        PublishedAt = version.PublishedAt,
        PublishedBy = version.PublishedBy,
        ArchivedAt = version.ArchivedAt,
        ArchivedBy = version.ArchivedBy,
        RowVersion = version.RowVersion,
        Authorship = Authorship(version),
    };

    public static Authorship Authorship(StageEntity row) => new(row.CreatedAt, row.CreatedBy, row.UpdatedAt, row.UpdatedBy);

    public static Authorship Authorship(StageVersionEntity row) => new(row.CreatedAt, row.CreatedBy, row.UpdatedAt, row.UpdatedBy);

    public static Authorship Authorship(PipelineEntity row) => new(row.CreatedAt, row.CreatedBy, row.UpdatedAt, row.UpdatedBy);

    public static Authorship Authorship(PipelineVersionEntity row) => new(row.CreatedAt, row.CreatedBy, row.UpdatedAt, row.UpdatedBy);

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
