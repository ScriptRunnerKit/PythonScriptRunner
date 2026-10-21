# Configuration reference

English | [Português](../pt-br/configuration.md)

Options are set in code, in the callback of each `Add...` method, or bound from configuration. The ready-made hosts bind
these sections, and you can do the same in your application:

```csharp
var configuration = builder.Configuration;
builder.Services
    .AddPythonScriptRunner(o => configuration.GetSection("PythonScriptRunner").Bind(o))
    .AddEntityFrameworkCore(o =>
    {
        o.UseNpgsql(configuration.GetConnectionString("psr")!);
        configuration.GetSection("PythonScriptRunner:EntityFrameworkCore").Bind(o);
    })
    .AddRabbitMq(o =>
    {
        o.ConnectionString = configuration.GetConnectionString("rabbitmq");
        configuration.GetSection("PythonScriptRunner:RabbitMQ").Bind(o);
    })
    .AddWorker(o => configuration.GetSection("PythonScriptRunner:Worker").Bind(o));
```

In environment variables, sections are separated by `__`: `PythonScriptRunner__Worker__MaxConcurrency=8`. Time values use
the `hh:mm:ss` format (`00:10:00`).

## Runner (`PythonScriptRunner`)

| Option | Default | Description |
|--------|---------|-------------|
| `WorkRoot` | `%TEMP%/psr` | Folder for the work and result folders of the attempts. |
| `EnvironmentCacheRoot` | `%LOCALAPPDATA%/psr` | Folder where Python environments and lock files are cached. |
| `UvPath` | `uv` | Path or name of the `uv` executable (searched in the PATH). |
| `DefaultPythonVersion` | `3.12` | Python version of stages that do not declare one. |
| `DefaultTimeout` | 5 minutes | Timeout of stages that do not declare one. |
| `DefaultMemoryLimitMb` | `1024` | Memory limit of stages that do not declare one. |
| `MaxFileBytes` | 50 MB | Maximum size of one file. |
| `MaxRunFilesBytes` | 200 MB | Maximum size of all files of one run. |
| `MaxLogBytes` | 1 MB | Bytes of stdout and stderr kept per attempt (the tail). |
| `MaxConcurrentProcesses` | processor count | Python processes running at the same time in this process. |
| `ThreadsPerProcess` | not set | Threads of the numeric libraries in each process. When not set: 1 on Linux when the memory limit falls back to `RLIMIT_AS`, no limit otherwise. See [Memory limits](hosting.md#memory-limits). |
| `PackageIndexUrl` | PyPI | Package index (pip `--index-url`). |
| `ExtraIndexUrls` | none | Extra package indexes (pip `--extra-index-url`). |
| `PsrunnerRequirement` | `psrunner==0.1.0` | The SDK installed in every environment: a pip requirement or the path of a local wheel. |
| `OrphanRetention` | 24 hours | Age after which leftover work folders are deleted. |

Secrets are read from `PythonScriptRunner:Secrets:<NAME>`.

## Persistence (`PythonScriptRunner:EntityFrameworkCore`)

| Option | Default | Description |
|--------|---------|-------------|
| `Retention:Artifacts` | 7 days | How long file contents are kept. |
| `Retention:Runs` | 90 days | How long runs, steps, attempts, logs and file metadata are kept. |
| `Retention:Audit` | 365 days | How long audit events are kept. |
| `Maintenance:Enabled` | `true` | Runs the maintenance job (partitions, retention, cleanups) in this process. |
| `Maintenance:Interval` | 1 hour | How often the maintenance job runs. |
| `Maintenance:BatchSize` | `1000` | Rows handled per batch by the maintenance job. |
| `IdempotencyWindow` | 24 hours | How long an idempotency key returns the same run. |
| `LatestVersionCacheDuration` | 30 seconds | How long "latest published version" lookups are cached in this process. |
| `UseDatabaseClock` | `true` | Uses the PostgreSQL clock for leases, retry delays, schedules and the outbox, so hosts with skewed clocks agree. |

The connection is set in code with `o.UseNpgsql(connectionString)`.

## RabbitMQ (`PythonScriptRunner:RabbitMQ`)

| Option | Default | Description |
|--------|---------|-------------|
| `ConnectionString` | none | AMQP URI: `amqp://user:password@host:5672/vhost`, or `amqps://` for TLS. |
| `DeclareTopology` | `true` | Declares exchanges and queues at startup. With `false`, startup only checks that they exist (for brokers managed by an operator). |
| `RunOutboxRelay` | `true` | Publishes the outbox from this process. Several instances can run it. |
| `EnablePublicCommands` | `true` | Consumes the public `StartPipelineRun` command. |
| `InstanceId` | machine name and process id | Names the per-instance queues of this process. |
| `RelayInterval` | 250 ms | How often the relay looks for messages when the outbox is idle. |
| `RelayBatchSize` | `100` | Messages published per relay batch. |
| `DeliveryLimit` | `5` | Delivery attempts of a command before it goes to `psr.dead-letter`. |
| `StartupConnectTimeout` | 10 seconds | How long startup waits for the broker before continuing in the background. |

## Worker (`PythonScriptRunner:Worker`)

| Option | Default | Description |
|--------|---------|-------------|
| `WorkerId` | the RabbitMQ instance id | Identifier recorded on the attempts this worker runs. |
| `MaxConcurrency` | processor count | Steps executed at the same time by this worker. |
| `LeaseDuration` | 60 seconds | Lease of a running attempt; another worker recovers it when not renewed. |
| `LeaseRenewInterval` | 15 seconds | How often the lease of a running attempt is renewed. |
| `ReaperInterval` | 15 seconds | How often expired leases are looked for. |
| `MaxLeaseRecoveries` | `3` | Recoveries of one step after lost workers before it fails with `WorkerLost`. |
| `ShutdownGracePeriod` | 5 minutes | How long running steps may finish when the host stops. The platform must allow at least this long. |
| `WorkFolderCleanupInterval` | 1 hour | How often leftover work folders are removed. |
| `Scheduler:Enabled` | `true` | Fires schedules from this worker. Several workers never fire the same occurrence. |
| `Scheduler:PollInterval` | 5 seconds | How often due schedules are looked for (the firing precision). |
| `Scheduler:BatchSize` | `50` | Schedules fired per transaction. |

## Dashboard fixed login (`PythonScriptRunner:Dashboard:Login`)

| Option | Default | Description |
|--------|---------|-------------|
| `Users` | none | List of `{ Name, Role, PasswordHash }`. |
| `SlidingExpiration` | 8 hours | Session lifetime, renewed while the user is active. |
| `AbsoluteExpiration` | 24 hours | Maximum session lifetime. |
| `MaxFailedAttempts` | `5` | Failed logins in a row before the user is locked. |
| `LockoutDuration` | 15 minutes | How long a locked user waits. |
| `MaxAttemptsPerMinutePerIp` | `10` | Login attempts allowed per IP per minute. |

The same options exist in code on `UseFixedLogin(l => ...)`, which also has `PersistKeysInDatabase` (default `true`): it
keeps the cookie keys in PostgreSQL, so sessions work on every instance and survive restarts.
