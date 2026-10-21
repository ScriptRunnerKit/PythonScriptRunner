# How it works

English | [Português](../pt-br/how-it-works.md)

This page explains what happens when a stage runs, what the library guarantees, and how to observe it in production.

## One attempt, step by step

1. **Validate the inputs** against the contract. Invalid inputs stop here, and no Python process starts.
2. **Resolve the secrets** the stage declares. A missing secret fails the attempt with `SecretNotFound`.
3. **Prepare the environment.** uv creates (or reuses) the Python environment of the stage. Environments are keyed by a
   hash of the Python version, the locked requirements and the `psrunner` version. A file lock makes concurrent runs wait
   for a single creation, and an environment is used only after it was completely installed.
4. **Wait for a slot.** At most `MaxConcurrentProcesses` Python processes run at the same time in one .NET process.
5. **Start the process** in a folder of its own, with:
   - a clean environment: only the variables Python needs, never the variables of your application;
   - the declared secrets;
   - the inputs in a file.
6. **Apply the limits:** the timeout and the memory limit (see [Memory limits](hosting.md#memory-limits)). On timeout or
   cancellation, the whole process tree is stopped.
7. **Capture the output.** The end of stdout and stderr is kept (up to `MaxLogBytes`), with secret values masked as `***`.
8. **Validate the outputs** against the contract and convert them to .NET values. Output files are moved to the run,
   within `MaxFileBytes` and `MaxRunFilesBytes`.

The .NET side and `psrunner` exchange files in protocol version 1, and `psrunner` stays compatible with it for as long
as that protocol is in use.

## Guarantees

- **Isolation:** each attempt runs in its own process, so a crash in Python never takes the .NET application down.
- **Limits:** every attempt has a timeout and a memory limit.
- **Secrets:** they reach only the process of the stage that declares them. They are never written to the inputs file,
  logs, metrics, exceptions or the database.
- **Reproducibility:** published dependencies are locked with hashes and installed with `--require-hashes`, so a
  published version always runs with the same packages.
- **Typed results:** the .NET side receives values that match the contract, or a clear error.

## Security model

PythonScriptRunner runs code written by **trusted authors**. A stage is arbitrary Python running with the permissions of
the worker's user; it is not a sandbox for untrusted code. Anyone with the `Author` role in the dashboard can publish code
that runs on the workers, so give that role only to people you would let deploy code.

Recommended hardening:
- Run workers as an unprivileged user with access only to what the stages need. The images already run as a non-root
  user with a read-only root file system.
- Keep secrets in your platform's secret store.
- Serve the dashboard over HTTPS.
- Do not expose the development setup of the AppHost or of the generated compose.

To report a vulnerability, see [SECURITY.md](../../SECURITY.md).

## Observability

**Traces.** The `ActivitySource` named `PythonScriptRunner` creates these spans:
- `psr.run.enqueue`: a run was queued;
- `psr.message.consume`: a message was handled;
- `psr.environment.ensure`: an environment was prepared;
- `psr.stage.execute`: an attempt ran.

The `traceparent` travels with the messages, so one trace goes from the HTTP request to the worker and every Python
process.

![Trace of a run](../images/aspire-trace.png)

**Metrics.** The `Meter` named `PythonScriptRunner` publishes these instruments:

| Instrument | Meaning |
|------------|---------|
| `psr.step.duration` | Duration of each attempt. |
| `psr.step.overhead` | Time spent by the runner outside the Python process. |
| `psr.step.failures` | Failed attempts. |
| `psr.environment.created` | Python environments created. |
| `psr.queue.wait` | Time from queuing a step to its start on a worker. |
| `psr.worker.active_steps` | Steps running on this worker. |
| `psr.lease.recovered` | Attempts restarted after their worker was lost. |
| `psr.schedule.fired` | Runs created by schedules. |
| `psr.outbox.publish.duration` | Time to publish one outbox batch. |
| `psr.maintenance.partitions.created`, `psr.maintenance.partitions.dropped`, `psr.maintenance.rows.deleted` | Work of the maintenance job. |

Add the source and the meter to your OpenTelemetry setup:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("PythonScriptRunner"))
    .WithMetrics(m => m.AddMeter("PythonScriptRunner"));
```

**Health checks:**
- `AddPythonScriptRunnerDatabase()` checks PostgreSQL;
- `AddPythonScriptRunnerBroker()` checks RabbitMQ;
- `AddPythonScriptRunnerChecks()` checks the database, the broker and uv together.

**Logs.** The library logs through `ILogger`. SQL commands are logged at `Debug`, because the outbox relay polls the
database several times per second.
