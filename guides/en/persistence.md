# Persistence (PostgreSQL)

English | [Português](../pt-br/persistence.md)

With `PythonScriptRunner.EntityFrameworkCore`, stages and pipelines live in PostgreSQL with immutable versions, and every
run is recorded. This level does not need RabbitMQ: synchronous runs are recorded too.

## Setup

```bash
dotnet add package PythonScriptRunner.EntityFrameworkCore
```

```csharp
builder.Services
    .AddPythonScriptRunner()
    .AddEntityFrameworkCore(o =>
    {
        o.UseNpgsql(builder.Configuration.GetConnectionString("psr")!);   // PostgreSQL 16 or newer
        o.Retention.Artifacts = TimeSpan.FromDays(7);                     // file contents
        o.Retention.Runs = TimeSpan.FromDays(90);                         // runs, steps, attempts and logs
    });
builder.Services.AddHealthChecks().AddPythonScriptRunnerDatabase();

var app = builder.Build();
await app.Services.MigratePythonScriptRunnerAsync();
```

The schema is never changed implicitly. You have two options:
- **At startup:** call `MigratePythonScriptRunnerAsync()`. Concurrent instances wait on a PostgreSQL advisory lock, so
  the migrations run once.
- **Through your DBA:** generate the SQL with `GeneratePythonScriptRunnerMigrationScriptAsync(idempotent: true)` and hand
  it over.

Everything is created in the `psr` schema.

## The catalog

With persistence, the catalog comes from the database. Each stage and pipeline has versions that move from **draft** to
**published** to **archived**. A published version never changes; editing creates a new draft.

`ICatalogAdmin` manages the catalog. The dashboard uses the same service.

```csharp
var admin = app.Services.GetRequiredService<ICatalogAdmin>();
var actor = Actor.User("ana@example.com");

var draft = await admin.CreateStageAsync(new NewCatalogItem("average", "Average"), actor);
var saved = await admin.SaveStageDraftAsync("average", draft.Version, draft.Data with
{
    Script = script,
    Inputs = [new FieldDefinition("values", FieldType.ListOf(FieldType.Number))],
    Outputs = [new FieldDefinition("average", FieldType.Number)],
}, draft.RowVersion, actor);
await admin.PublishStageAsync("average", saved.Version, saved.RowVersion, actor);
```

| Operation | Methods |
|-----------|---------|
| Create and edit drafts | `CreateStageAsync`, `CreateStageDraftFromAsync`, `SaveStageDraftAsync`, `DiscardStageDraftAsync` (and the `Pipeline` equivalents) |
| Validate and test a draft | `ValidateStageDraftAsync`, `TestStageDraftAsync`, `EnqueueStageDraftTestAsync` (on a worker) |
| Publish | `PublishStageAsync`, `PublishPipelineAsync` |
| Archive and delete | `ArchiveStageVersionAsync`, `DeleteStageAsync` (and the `Pipeline` equivalents) |
| Import and export | `ImportAsync(zip)` creates drafts from a zip in the folder format; `ExportStageVersionAsync` and `ExportPipelineVersionAsync` |
| Schedules | `CreateScheduleAsync`, `UpdateScheduleAsync`, `SetScheduleEnabledAsync`, `DeleteScheduleAsync` (see [Queue, worker and scheduling](queue-and-worker.md)) |

Details of these operations:
- **Optimistic concurrency:** saves use the row version, so two people editing the same draft cannot overwrite each other
  silently.
- **Publishing a stage:**
  1. checks the script syntax with the declared Python;
  2. locks the requirements with hashes;
  3. freezes the version.
- **Publishing a pipeline:** validates the types and bindings against the published stages.
- **Bringing in a folder:** to move a folder catalog into the database, zip the folder (with `stages/` and `pipelines/` at
  the root), import it, then publish the stages and the pipelines.

## Runs

Every run is stored, whether it was synchronous (`IPythonRunner`), queued, scheduled or started from the dashboard. The
record includes the inputs, the steps, every attempt with its duration and error, the tail of stdout and stderr (up to
`MaxLogBytes`) and the files. File contents are stored in PostgreSQL (`bytea`), streamed in and out.

`IRunManager` reads and manages them:

| Method | Description |
|--------|-------------|
| `GetAsync(runId)` | The full run with its steps and attempts. |
| `ListAsync(query)` | Runs filtered by status, pipeline, stage, trigger or period, with keyset pagination. |
| `GetAttemptLogAsync(runId, attemptId, stream)` | The captured stdout or stderr of an attempt. |
| `CancelAsync(runId, actor)` | Cancels a run. A queued run stops on the worker that is running it. |
| `ReprocessAsync(runId, actor, mode)` | Retries a failed run with the same version and inputs. The steps that succeeded keep their outputs and the step that failed gets a new attempt. `ExecutionMode.Async` (the default) queues it for the workers and needs RabbitMQ; `ExecutionMode.Sync` runs it now in the current process. |

**Idempotency:** `RunOptions.IdempotencyKey` returns the same run when the same key is used again within
`IdempotencyWindow` (24 hours by default). It is useful when the caller may repeat a request.

## Retention

History is partitioned: runs by week, file contents by day, and the audit log by month. A maintenance job, on by default
and running every hour, creates partitions ahead of time and drops the expired ones, which is cheap even with large
volumes.

| Option | Default |
|--------|---------|
| `Retention.Artifacts` (file contents) | 7 days |
| `Retention.Runs` (runs, steps, attempts, logs, file metadata) | 90 days |
| `Retention.Audit` | 365 days |
| `Maintenance.Enabled`, `Maintenance.Interval` | `true`, 1 hour |

A pipeline can have a shorter retention than the global one, but not a longer one.

## Audit

Human actions are recorded in `psr.audit_events`, with who did what, on which item and when. That covers editing,
publishing, archiving, deleting, cancelling, retrying, scheduling and changing settings. The actor of each action is an
`Actor`:
- `Actor.User("subject")` for people;
- `Actor.System("origin")` for automatic triggers, which are recorded on the run itself.
