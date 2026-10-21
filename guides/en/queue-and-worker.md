# Queue, worker and scheduling (RabbitMQ)

English | [Português](../pt-br/queue-and-worker.md)

With `PythonScriptRunner.RabbitMQ` and `PythonScriptRunner.Worker`, runs happen in the background. Your application
enqueues a run and continues; workers, on one machine or many, execute the steps. This level needs
[persistence](persistence.md): PostgreSQL holds the state of every run and the outbox of messages.

## Setup

```bash
dotnet add package PythonScriptRunner.EntityFrameworkCore
dotnet add package PythonScriptRunner.RabbitMQ
dotnet add package PythonScriptRunner.Worker
```

```csharp
builder.Services
    .AddPythonScriptRunner()
    .AddEntityFrameworkCore(o => o.UseNpgsql(builder.Configuration.GetConnectionString("psr")!))
    .AddRabbitMq(o => o.ConnectionString = builder.Configuration.GetConnectionString("rabbitmq"))   // RabbitMQ 3.13 or newer
    .AddWorker(o => o.MaxConcurrency = 4);
builder.Services.AddHealthChecks().AddPythonScriptRunnerChecks();   // database, broker and uv
```

`AddWorker` goes only in the processes that should execute steps. A web application can enqueue runs and show their
status with `AddRabbitMq` alone, while dedicated worker processes (or the [worker image](hosting.md)) do the work.

## Enqueuing a run

```csharp
var queue = app.Services.GetRequiredService<IRunQueue>();

RunHandle run = await queue.EnqueuePipelineAsync("progression-calc",
    new { a1 = 1, ratio = 2, n = 4, coefficients = new[] { 1.0, 0.0, 1.0 } },
    new RunOptions { IdempotencyKey = "order-1234" });
```

`EnqueuePipelineAsync` and `EnqueueStageAsync` validate the inputs, record the run and return a handle with its id. Follow
the run with `IRunManager.GetAsync(run.RunId)`, with [events](#events), or in the [dashboard](dashboard.md).

## How a run moves

1. The run and the message of its first step are written in one PostgreSQL transaction (an outbox). Enqueuing works
   even while RabbitMQ is down; the message is sent when the broker comes back.
2. A worker takes the step with a **lease**, runs the Python process and keeps renewing the lease while it runs.
3. When the step ends, the worker writes the result and the message of the next step in the same transaction.
4. Events about the run are published as it progresses.

## Reliability

- **Retry:** a failed attempt is retried according to the stage's `retry` policy (see [Stages](stages.md#retry)). The run
  waits for the delay without holding a worker.
- **Lost workers:** if a worker stops renewing its lease (it crashed, or its machine went away), the step is taken back.
  - A stage marked `idempotent`, or one that still has retries left, is resumed by another worker.
  - Otherwise the step fails with `WorkerLost`, for a manual retry.
- **Duplicates:** a message delivered twice never runs a step twice.
- **Poison messages:** after `DeliveryLimit` failed deliveries (5), a command goes to the `psr.dead-letter` queue.
- **Graceful shutdown:** a stopping worker takes no new steps and waits up to `ShutdownGracePeriod` (5 minutes) for the
  running ones.

## Concurrency and cancellation

- **Per worker:** `MaxConcurrency` limits how many steps a worker runs at once. The default is the processor count.
- **Per pipeline:** a maximum number of concurrent runs can be set. Extra runs wait as `Pending`, in order.
- **Cancellation:** `IRunManager.CancelAsync` stops the process tree on the worker that is running the step, wherever it
  is.

## Scheduling

Schedules run a pipeline from a cron expression, in an IANA time zone:

```csharp
var admin = app.Services.GetRequiredService<ICatalogAdmin>();
await admin.CreateScheduleAsync("progression-calc", new ScheduleData
{
    Name = "Every business day at 2 AM",
    Cron = "0 2 * * 1-5",                  // 5 fields, or 6 with seconds
    TimeZone = "America/Sao_Paulo",        // the default; daylight saving time is handled
    Inputs = new JsonObject { ["a1"] = 1, ["ratio"] = 2, ["n"] = 4, ["coefficients"] = new JsonArray(1, 0, 1) },
}, Actor.User("ana@example.com"));
```

How schedules behave:
- **Version:** without `PinnedVersion`, each firing runs the latest published version.
- **Single firing:** several workers never fire the same occurrence.
- **Missed occurrences:** if they were missed while every worker was down, they become a single catch-up run.
- **File inputs:** a pipeline with a required `file` input cannot be scheduled.
- **Scheduler options:** the scheduler runs inside the workers, under `Scheduler` in the worker options (`Enabled`,
  `PollInterval`, `BatchSize`).

## Events

Every run publishes events to the `psr.events` topic exchange:

| Event | When |
|-------|------|
| `run.queued`, `run.rejected`, `run.started` | The run was accepted, refused (for example, invalid inputs) or started. |
| `step.started`, `step.succeeded`, `step.failed`, `step.retry-scheduled` | Progress of each step. |
| `run.succeeded`, `run.failed`, `run.cancelled` | The run ended. `run.succeeded` carries the outputs. |

The routing key is `<event>.<pipeline key>` (or `<event>.stage:<stage key>` for single-stage runs), so you can subscribe to
exactly what you need. Inside .NET, handle them with `IRunEventHandler`:

```csharp
builder.Services.AddPythonScriptRunner()
    .AddRabbitMq(o => o.ConnectionString = builder.Configuration.GetConnectionString("rabbitmq"))
    .AddRunEventHandler<NotifyErp>(s => s
        .Subscription("erp")                                   // one durable queue shared by all instances
        .On("run.succeeded.progression-calc", "run.failed.*"));

public sealed class NotifyErp : IRunEventHandler
{
    public Task HandleAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
        // runEvent.Type, runEvent.RunId, runEvent.Outputs, runEvent.Error, ...
        return Task.CompletedTask;
    }
}
```

Throwing from `HandleAsync` makes the broker deliver the event again, up to the delivery limit. Use `PerInstance(prefix)`
instead of `Subscription(name)` when every instance must receive every event.

## Systems written in other languages

Any system that talks to RabbitMQ can start pipelines and follow them, without .NET:

- **Start a run:**
  - publish a `StartPipelineRun` JSON message to the `psr.public` exchange, with routing key `pipeline.start`;
  - fields: `pipelineKey` (required), `version`, `inputs`, `idempotencyKey`, `correlationId`, `requestedBy`;
  - file inputs are not accepted through this command.
- **Follow it:** bind your own queue to the `psr.events` exchange with the routing keys you want, for example
  `run.*.progression-calc`. The `correlationId` you sent comes back in every event.

The message contracts are JSON Schemas in
[`schemas/messages`](../../schemas/messages): `start-pipeline-run.v1.json` and `run-event.v1.json`.

## Observability

The `traceparent` follows the run from the caller to the worker, so one distributed trace shows the request, the queue
and every Python process. See [How it works](how-it-works.md#observability).
