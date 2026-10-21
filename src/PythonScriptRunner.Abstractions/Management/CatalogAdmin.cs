using System.Text.Json.Nodes;

namespace PythonScriptRunner;

/// <summary>
/// Edits the catalog stored in a database: drafts, publication, archiving, deletion, settings and import/export.
/// Every change receives the <see cref="Actor"/> that performs it (authorship and audit of human actions).
/// </summary>
/// <remarks>Errors are reported with <see cref="PsrDomainException"/> codes (<see cref="PsrErrorCodes"/>).</remarks>
public interface ICatalogAdmin
{
    /// <summary>Lists stages.</summary>
    /// <param name="query">Search and page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Page<StageSummary>> ListStagesAsync(CatalogQuery query, CancellationToken cancellationToken = default);

    /// <summary>Reads a stage with its versions and the pipelines that use it.</summary>
    /// <param name="key">Stage key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stage, or <c>null</c> when it does not exist.</returns>
    Task<StageDetail?> GetStageAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Reads one version of a stage (any status).</summary>
    /// <param name="key">Stage key.</param>
    /// <param name="version">Version number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<StageVersionDetail?> GetStageVersionAsync(string key, int version, CancellationToken cancellationToken = default);

    /// <summary>Reads the current draft of a stage.</summary>
    /// <param name="key">Stage key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<StageDraft?> GetStageDraftAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Creates a stage with an initial draft (version 1). <c>STAGE_KEY_TAKEN</c> when the key exists.</summary>
    /// <param name="stage">Key, name and description.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<StageDraft> CreateStageAsync(NewCatalogItem stage, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Changes the name or description of a stage.</summary>
    /// <param name="key">Stage key.</param>
    /// <param name="changes">New values (<c>null</c> keeps the current one).</param>
    /// <param name="expectedRowVersion">Row version read by the caller.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<StageDetail> UpdateStageAsync(
        string key, CatalogItemChanges changes, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Creates a draft (next version) with the content of an existing version. <c>DRAFT_ALREADY_EXISTS</c> when there is one.</summary>
    /// <param name="key">Stage key.</param>
    /// <param name="fromVersion">Version to copy.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<StageDraft> CreateStageDraftFromAsync(string key, int fromVersion, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Saves a draft. <c>VERSION_IMMUTABLE</c> when the version is not a draft; <c>CONCURRENCY_CONFLICT</c> when it changed.</summary>
    /// <param name="key">Stage key.</param>
    /// <param name="version">Draft version.</param>
    /// <param name="data">New content.</param>
    /// <param name="expectedRowVersion">Row version read by the caller.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<StageDraft> SaveStageDraftAsync(
        string key, int version, StageDraftData data, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Deletes a draft.</summary>
    /// <param name="key">Stage key.</param>
    /// <param name="version">Draft version.</param>
    /// <param name="expectedRowVersion">Row version read by the caller.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DiscardStageDraftAsync(string key, int version, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Validates the draft contract (fields, limits, secrets) without compiling or locking.</summary>
    /// <param name="key">Stage key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ValidationReport> ValidateStageDraftAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a draft: validates the contract, compiles the script with its Python version and locks the dependencies with uv.
    /// Errors: <c>STAGE_INVALID</c>, <c>SCRIPT_SYNTAX_ERROR</c>, <c>DEPENDENCY_RESOLUTION_FAILED</c>, <c>CONCURRENCY_CONFLICT</c>.
    /// </summary>
    /// <param name="key">Stage key.</param>
    /// <param name="version">Draft version.</param>
    /// <param name="expectedRowVersion">Row version read by the caller.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<StageVersionDetail> PublishStageAsync(
        string key, int version, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Archives a published version. <c>VERSION_REFERENCED</c> when a published pipeline uses it.</summary>
    /// <param name="key">Stage key.</param>
    /// <param name="version">Published version.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<StageVersionDetail> ArchiveStageVersionAsync(string key, int version, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Deletes a stage and its versions. <c>DELETE_NOT_ALLOWED</c> when referenced by a pipeline step or present in the retained history.</summary>
    /// <param name="key">Stage key.</param>
    /// <param name="actor">Administrator.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteStageAsync(string key, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Runs the current draft as a test (<see cref="RunKind.StageTest"/>) and waits for the result.</summary>
    /// <param name="key">Stage key.</param>
    /// <param name="inputs">Inputs (same forms as <see cref="IPythonRunner.RunStageAsync"/>).</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    Task<RunResult> TestStageDraftAsync(string key, object? inputs, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Writes a version as a zip in the folder catalog format (<c>stages/&lt;key&gt;@&lt;version&gt;/stage.json</c> + files).</summary>
    /// <param name="key">Stage key.</param>
    /// <param name="version">Version.</param>
    /// <param name="destination">Writable stream that receives the zip.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ExportStageVersionAsync(string key, int version, Stream destination, CancellationToken cancellationToken = default);

    /// <summary>Lists pipelines.</summary>
    /// <param name="query">Search and page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Page<PipelineSummary>> ListPipelinesAsync(CatalogQuery query, CancellationToken cancellationToken = default);

    /// <summary>Reads a pipeline with its versions and settings.</summary>
    /// <param name="key">Pipeline key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PipelineDetail?> GetPipelineAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Reads one version of a pipeline (any status).</summary>
    /// <param name="key">Pipeline key.</param>
    /// <param name="version">Version number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PipelineVersionDetail?> GetPipelineVersionAsync(string key, int version, CancellationToken cancellationToken = default);

    /// <summary>Reads the current draft of a pipeline.</summary>
    /// <param name="key">Pipeline key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PipelineDraft?> GetPipelineDraftAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Creates a pipeline with an empty draft (version 1). <c>PIPELINE_KEY_TAKEN</c> when the key exists.</summary>
    /// <param name="pipeline">Key, name and description.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PipelineDraft> CreatePipelineAsync(NewCatalogItem pipeline, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Changes the name or description of a pipeline.</summary>
    /// <param name="key">Pipeline key.</param>
    /// <param name="changes">New values (<c>null</c> keeps the current one).</param>
    /// <param name="expectedRowVersion">Row version read by the caller.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PipelineDetail> UpdatePipelineAsync(
        string key, CatalogItemChanges changes, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Creates a draft (next version) with the content of an existing version.</summary>
    /// <param name="key">Pipeline key.</param>
    /// <param name="fromVersion">Version to copy.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PipelineDraft> CreatePipelineDraftFromAsync(string key, int fromVersion, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Saves a draft. Steps must reference existing stage versions (published to be publishable).</summary>
    /// <param name="key">Pipeline key.</param>
    /// <param name="version">Draft version.</param>
    /// <param name="data">New content.</param>
    /// <param name="expectedRowVersion">Row version read by the caller.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PipelineDraft> SavePipelineDraftAsync(
        string key, int version, PipelineDraftData data, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Deletes a draft.</summary>
    /// <param name="key">Pipeline key.</param>
    /// <param name="version">Draft version.</param>
    /// <param name="expectedRowVersion">Row version read by the caller.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DiscardPipelineDraftAsync(string key, int version, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Publishes a draft after validation (<c>PIPELINE_INVALID</c> with the report); warnings are returned.</summary>
    /// <param name="key">Pipeline key.</param>
    /// <param name="version">Draft version.</param>
    /// <param name="expectedRowVersion">Row version read by the caller.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PipelinePublication> PublishPipelineAsync(
        string key, int version, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Archives a published version. <c>VERSION_SCHEDULED</c> when an enabled schedule pins it.</summary>
    /// <param name="key">Pipeline key.</param>
    /// <param name="version">Published version.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PipelineVersionDetail> ArchivePipelineVersionAsync(string key, int version, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Deletes a pipeline, its versions and schedules. <c>DELETE_NOT_ALLOWED</c> when it has runs in the retained history.</summary>
    /// <param name="key">Pipeline key.</param>
    /// <param name="actor">Administrator.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeletePipelineAsync(string key, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Changes operational settings (not versioned).</summary>
    /// <param name="key">Pipeline key.</param>
    /// <param name="settings">New settings.</param>
    /// <param name="expectedRowVersion">Row version read by the caller.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PipelineDetail> UpdatePipelineSettingsAsync(
        string key, PipelineSettings settings, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Shortens the retention of the pipeline runs and files. <c>RETENTION_EXCEEDS_GLOBAL</c> when longer than the global one.</summary>
    /// <param name="key">Pipeline key.</param>
    /// <param name="retention">New retention (<c>null</c> uses the global value).</param>
    /// <param name="expectedRowVersion">Row version read by the caller.</param>
    /// <param name="actor">Administrator.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PipelineDetail> UpdatePipelineRetentionAsync(
        string key, PipelineRetention retention, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the current draft of a stage and queues it as a test (<see cref="RunKind.StageTest"/>) for the workers (requires the
    /// RabbitMQ package; D-114). <c>STAGE_INVALID</c> when invalid; <see cref="InputValidationException"/> for invalid inputs.
    /// </summary>
    /// <param name="key">Stage key.</param>
    /// <param name="inputs">Stage inputs.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RunHandle> EnqueueStageDraftTestAsync(string key, object? inputs, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the current draft of a pipeline and queues it as a test (<see cref="RunKind.PipelineTest"/>; D-97, D-114).
    /// <c>PIPELINE_INVALID</c> when invalid; <see cref="InputValidationException"/> for invalid inputs.
    /// </summary>
    /// <param name="key">Pipeline key.</param>
    /// <param name="inputs">Pipeline inputs.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RunHandle> EnqueuePipelineDraftTestAsync(string key, object? inputs, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Validates and runs the current draft as a test (<see cref="RunKind.PipelineTest"/>); <c>PIPELINE_INVALID</c> when invalid.</summary>
    /// <param name="key">Pipeline key.</param>
    /// <param name="inputs">Pipeline inputs.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    Task<RunResult> TestPipelineDraftAsync(string key, object? inputs, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Writes a version and the stage versions it uses as a zip in the folder catalog format.</summary>
    /// <param name="key">Pipeline key.</param>
    /// <param name="version">Version.</param>
    /// <param name="destination">Writable stream that receives the zip.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ExportPipelineVersionAsync(string key, int version, Stream destination, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a zip in the folder catalog format as drafts: new keys get version 1, existing keys get a draft of a new version
    /// (published versions are never overwritten). All or nothing: <c>IMPORT_INVALID</c> lists every problem.
    /// </summary>
    /// <param name="zip">Readable zip stream.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ImportReport> ImportAsync(Stream zip, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Lists schedules ordered by name.</summary>
    /// <param name="query">Pipeline filter and page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Page<ScheduleDetail>> ListSchedulesAsync(ScheduleQuery query, CancellationToken cancellationToken = default);

    /// <summary>Reads a schedule.</summary>
    /// <param name="id">Schedule identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The schedule, or <c>null</c> when it does not exist.</returns>
    Task<ScheduleDetail?> GetScheduleAsync(ScheduleId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a cron schedule of a pipeline. Validates the cron expression, the IANA time zone, the version (pinned or the latest
    /// published) and the fixed inputs: <c>SCHEDULE_INVALID</c>, <c>SCHEDULE_FILE_INPUT</c> (pipelines with file inputs cannot be
    /// scheduled) or <see cref="InputValidationException"/>.
    /// </summary>
    /// <param name="pipelineKey">Pipeline key.</param>
    /// <param name="schedule">Schedule settings.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ScheduleDetail> CreateScheduleAsync(string pipelineKey, ScheduleData schedule, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Changes a schedule (same validation as <see cref="CreateScheduleAsync"/>); the next run is recomputed.</summary>
    /// <param name="id">Schedule identifier.</param>
    /// <param name="schedule">New settings.</param>
    /// <param name="expectedRowVersion">Row version read by the caller.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ScheduleDetail> UpdateScheduleAsync(
        ScheduleId id, ScheduleData schedule, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Enables or disables a schedule; enabling recomputes the next run from now.</summary>
    /// <param name="id">Schedule identifier.</param>
    /// <param name="enabled">Whether it fires.</param>
    /// <param name="expectedRowVersion">Row version read by the caller.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ScheduleDetail> SetScheduleEnabledAsync(
        ScheduleId id, bool enabled, uint expectedRowVersion, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Deletes a schedule (its runs stay in the history).</summary>
    /// <param name="id">Schedule identifier.</param>
    /// <param name="actor">Author.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteScheduleAsync(ScheduleId id, Actor actor, CancellationToken cancellationToken = default);
}

/// <summary>Search and page of a catalog list.</summary>
/// <param name="Search">Text contained in the key or the name.</param>
/// <param name="PageNumber">1-based page number.</param>
/// <param name="PageSize">Items per page (1-100).</param>
/// <param name="Status">Only items that have a version with this status.</param>
/// <param name="UsedByPipeline">
/// Stages only: the stages used by the latest published version of this pipeline, in step order; a pipeline that does not
/// exist or has no published version gives an empty page. <see cref="ICatalogAdmin.ListPipelinesAsync"/> rejects it.
/// </param>
public sealed record CatalogQuery(
    string? Search = null, int PageNumber = 1, int PageSize = 25, VersionStatus? Status = null, string? UsedByPipeline = null);

/// <summary>A new stage or pipeline.</summary>
/// <param name="Key">Slug (immutable).</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">Description.</param>
public sealed record NewCatalogItem(string Key, string Name, string? Description = null);

/// <summary>Changes to the name or description; <c>null</c> keeps the current value.</summary>
/// <param name="Name">New name.</param>
/// <param name="Description">New description.</param>
public sealed record CatalogItemChanges(string? Name = null, string? Description = null);

/// <summary>A version in a list.</summary>
/// <param name="Version">Version number.</param>
/// <param name="Status">Status.</param>
/// <param name="PublishedAt">Publication time.</param>
/// <param name="UpdatedAt">Last change.</param>
public sealed record VersionSummary(int Version, VersionStatus Status, DateTimeOffset? PublishedAt, DateTimeOffset UpdatedAt);

/// <summary>Editable content of a stage version.</summary>
public sealed record StageDraftData
{
    /// <summary>Python source of the single script.</summary>
    public required string Script { get; init; }

    /// <summary>Python version (e.g. <c>3.12</c>).</summary>
    public required string PythonVersion { get; init; }

    /// <summary>Dependencies in requirements.txt format.</summary>
    public string? Requirements { get; init; }

    /// <summary>Declared inputs.</summary>
    public IReadOnlyList<FieldDefinition> Inputs { get; init; } = [];

    /// <summary>Declared outputs.</summary>
    public IReadOnlyList<FieldDefinition> Outputs { get; init; } = [];

    /// <summary>Names of the secrets the script needs.</summary>
    public IReadOnlyList<string> RequiredSecrets { get; init; } = [];

    /// <summary>Maximum duration of one attempt.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Memory limit in MB; <c>null</c> uses the runner default.</summary>
    public int? MemoryLimitMb { get; init; }

    /// <summary>Automatic retry policy.</summary>
    public RetryPolicy Retry { get; init; } = RetryPolicy.None;

    /// <summary>Whether running the script twice with the same inputs is safe.</summary>
    public bool Idempotent { get; init; }
}

/// <summary>The current draft of a stage.</summary>
/// <param name="Key">Stage key.</param>
/// <param name="Version">Draft version.</param>
/// <param name="Data">Content.</param>
/// <param name="RowVersion">Concurrency token (send it back when saving).</param>
/// <param name="Authorship">Creation and last change.</param>
public sealed record StageDraft(string Key, int Version, StageDraftData Data, uint RowVersion, Authorship Authorship);

/// <summary>One version of a stage.</summary>
public sealed record StageVersionDetail
{
    /// <summary>Stage key.</summary>
    public required string Key { get; init; }

    /// <summary>Version number.</summary>
    public required int Version { get; init; }

    /// <summary>Status.</summary>
    public required VersionStatus Status { get; init; }

    /// <summary>Content.</summary>
    public required StageDraftData Data { get; init; }

    /// <summary>SHA-256 of the script.</summary>
    public required string ScriptSha256 { get; init; }

    /// <summary>Locked dependencies (published versions).</summary>
    public string? LockedRequirements { get; init; }

    /// <summary>Hash of the Python environment (published versions).</summary>
    public string? EnvironmentHash { get; init; }

    /// <summary>Publication time.</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Who published.</summary>
    public string? PublishedBy { get; init; }

    /// <summary>Archiving time.</summary>
    public DateTimeOffset? ArchivedAt { get; init; }

    /// <summary>Who archived.</summary>
    public string? ArchivedBy { get; init; }

    /// <summary>Concurrency token.</summary>
    public required uint RowVersion { get; init; }

    /// <summary>Creation and last change.</summary>
    public required Authorship Authorship { get; init; }
}

/// <summary>A stage in a list.</summary>
/// <param name="Key">Stage key.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">Description.</param>
/// <param name="LatestPublishedVersion">Latest published version.</param>
/// <param name="DraftVersion">Version of the current draft.</param>
/// <param name="UpdatedAt">Last change.</param>
/// <param name="UsedByPipelines">Distinct pipelines with a published version that uses any version of the stage.</param>
public sealed record StageSummary(
    string Key, string Name, string? Description, int? LatestPublishedVersion, int? DraftVersion, DateTimeOffset UpdatedAt, int UsedByPipelines);

/// <summary>A pipeline step that uses a stage version.</summary>
/// <param name="PipelineKey">Pipeline key.</param>
/// <param name="PipelineVersion">Pipeline version.</param>
/// <param name="PipelineStatus">Status of the pipeline version.</param>
/// <param name="StepKey">Step key.</param>
/// <param name="StageVersion">Stage version used by the step.</param>
public sealed record StageUsage(string PipelineKey, int PipelineVersion, VersionStatus PipelineStatus, string StepKey, int StageVersion);

/// <summary>A stage with its versions and usages.</summary>
/// <param name="Key">Stage key.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">Description.</param>
/// <param name="LatestPublishedVersion">Latest published version.</param>
/// <param name="Versions">Every version, newest first.</param>
/// <param name="UsedBy">Pipeline steps that use a version of the stage.</param>
/// <param name="RowVersion">Concurrency token of the stage row.</param>
/// <param name="Authorship">Creation and last change.</param>
public sealed record StageDetail(
    string Key,
    string Name,
    string? Description,
    int? LatestPublishedVersion,
    IReadOnlyList<VersionSummary> Versions,
    IReadOnlyList<StageUsage> UsedBy,
    uint RowVersion,
    Authorship Authorship);

/// <summary>Editable content of a pipeline version.</summary>
public sealed record PipelineDraftData
{
    /// <summary>Inputs received by the pipeline.</summary>
    public IReadOnlyList<FieldDefinition> Inputs { get; init; } = [];

    /// <summary>Steps in execution order.</summary>
    public IReadOnlyList<PipelineStepDefinition> Steps { get; init; } = [];

    /// <summary>Declared outputs.</summary>
    public IReadOnlyList<PipelineOutputDefinition> Outputs { get; init; } = [];

    /// <summary>Positions of nodes and edges in the visual editor (does not affect execution).</summary>
    public JsonNode? Layout { get; init; }
}

/// <summary>The current draft of a pipeline.</summary>
/// <param name="Key">Pipeline key.</param>
/// <param name="Version">Draft version.</param>
/// <param name="Data">Content.</param>
/// <param name="RowVersion">Concurrency token (send it back when saving).</param>
/// <param name="Authorship">Creation and last change.</param>
public sealed record PipelineDraft(string Key, int Version, PipelineDraftData Data, uint RowVersion, Authorship Authorship);

/// <summary>One version of a pipeline.</summary>
public sealed record PipelineVersionDetail
{
    /// <summary>Pipeline key.</summary>
    public required string Key { get; init; }

    /// <summary>Version number.</summary>
    public required int Version { get; init; }

    /// <summary>Status.</summary>
    public required VersionStatus Status { get; init; }

    /// <summary>Content.</summary>
    public required PipelineDraftData Data { get; init; }

    /// <summary>Publication time.</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Who published.</summary>
    public string? PublishedBy { get; init; }

    /// <summary>Archiving time.</summary>
    public DateTimeOffset? ArchivedAt { get; init; }

    /// <summary>Who archived.</summary>
    public string? ArchivedBy { get; init; }

    /// <summary>Concurrency token.</summary>
    public required uint RowVersion { get; init; }

    /// <summary>Creation and last change.</summary>
    public required Authorship Authorship { get; init; }
}

/// <summary>A pipeline in a list.</summary>
/// <param name="Key">Pipeline key.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">Description.</param>
/// <param name="LatestPublishedVersion">Latest published version.</param>
/// <param name="DraftVersion">Version of the current draft.</param>
/// <param name="UpdatedAt">Last change.</param>
public sealed record PipelineSummary(string Key, string Name, string? Description, int? LatestPublishedVersion, int? DraftVersion, DateTimeOffset UpdatedAt);

/// <summary>Operational settings of a pipeline (not versioned).</summary>
/// <param name="MaxConcurrentRuns">Maximum simultaneous asynchronous runs; <c>null</c> = no limit.</param>
public sealed record PipelineSettings(int? MaxConcurrentRuns);

/// <summary>Retention of a pipeline; it can only be shorter than the global retention.</summary>
/// <param name="ArtifactRetentionDays">Days the files are kept; <c>null</c> = global.</param>
/// <param name="RunRetentionDays">Days the runs are kept; <c>null</c> = global.</param>
public sealed record PipelineRetention(int? ArtifactRetentionDays, int? RunRetentionDays);

/// <summary>A pipeline with its versions and settings.</summary>
/// <param name="Key">Pipeline key.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">Description.</param>
/// <param name="LatestPublishedVersion">Latest published version.</param>
/// <param name="Versions">Every version, newest first.</param>
/// <param name="Settings">Operational settings.</param>
/// <param name="Retention">Retention.</param>
/// <param name="RowVersion">Concurrency token of the pipeline row.</param>
/// <param name="Authorship">Creation and last change.</param>
public sealed record PipelineDetail(
    string Key,
    string Name,
    string? Description,
    int? LatestPublishedVersion,
    IReadOnlyList<VersionSummary> Versions,
    PipelineSettings Settings,
    PipelineRetention Retention,
    uint RowVersion,
    Authorship Authorship);

/// <summary>Result of publishing a pipeline.</summary>
/// <param name="Version">The published version.</param>
/// <param name="Warnings">Validation warnings (they do not block publication).</param>
public sealed record PipelinePublication(PipelineVersionDetail Version, IReadOnlyList<ValidationIssue> Warnings);

/// <summary>A draft created by an import.</summary>
/// <param name="Kind"><c>Stage</c> or <c>Pipeline</c>.</param>
/// <param name="Key">Key.</param>
/// <param name="Version">Version of the created draft.</param>
/// <param name="NewKey">Whether the key did not exist before.</param>
public sealed record ImportedItem(string Kind, string Key, int Version, bool NewKey);

/// <summary>Result of an import.</summary>
/// <param name="Items">Drafts created.</param>
public sealed record ImportReport(IReadOnlyList<ImportedItem> Items);
