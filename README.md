# PythonScriptRunner

A .NET 10 library for running Python in a typed, composable and observable way. **Stages** declare an input/output
contract, **pipelines** chain them, and RabbitMQ runs them asynchronously. Cron scheduling and an optional Vue
dashboard are included.

> The project is under active development, heading for its first release (0.1.0). This version ships the **Core Runner**
> (synchronous stages and pipelines), the **`psrunner`** Python SDK, **PostgreSQL persistence** (versioned catalog and
> history), **asynchronous execution** with RabbitMQ, a worker and cron scheduling, the **embeddable dashboard** (Vue UI,
> API, real time and configurable access), ready-made **hosts and container images** and a local environment on
> **.NET Aspire**.

## How it works

Each stage declares its inputs and outputs and has exactly one Python script. There are 12 field types: `number`,
`integer`, `decimal`, `string`, `bool`, `date`, `datetime`, `enum`, `json`, `table`, `file` and `list<T>`. The runner
validates the inputs and runs the script in an isolated process: a clean environment, a timeout, a memory limit, and
secrets injected into that process only. It then validates the outputs and returns the typed result.

```python
# stages/progression-terms/script.py
from psrunner import stage

@stage
def run(inputs, ctx):
    a1, ratio, n = inputs["a1"], inputs["ratio"], inputs["n"]
    terms = [a1 * ratio**i for i in range(n)]
    ctx.log.info("generated %d terms", n)
    return {"terms": terms}
```

```jsonc
// stages/progression-terms/stage.json
{
  "$schema": "https://raw.githubusercontent.com/ScriptRunnerKit/PythonScriptRunner/main/schemas/stage.v1.json",
  "key": "progression-terms",
  "version": 1,
  "name": "Progression terms",
  "inputs": [
    { "name": "a1", "type": "number" },
    { "name": "ratio", "type": "number" },
    { "name": "n", "type": "integer" }
  ],
  "outputs": [ { "name": "terms", "type": "list", "itemType": "number" } ]
}
```

```csharp
builder.Services
    .AddPythonScriptRunner()
    .AddFileCatalog("./psr");                       // folder with stages/<key>/stage.json

// ...
await using var result = await runner.RunStageAsync("progression-terms", new { a1 = 1, ratio = 2, n = 4 });
result.EnsureSuccess();
double[] terms = result.GetOutput<double[]>("terms");   // [1, 2, 4, 8]
```

Stages can also be defined in C# (`AddStage("key", s => s.Script(...).Input(...).Output(...))`) and tested locally with
pytest through `psrunner.testing.run_stage`, without .NET.

## Pipelines

A pipeline chains stages in order. Each input of a step takes one of these:

- an input of the pipeline (`$pipeline.field`);
- an output of any earlier step (`$steps.step.field`);
- a fixed value (`{"const": ...}`).

Without an explicit binding, the field with the same name from the previous step is used. The pipeline declares its own
outputs. Types and bindings are validated when the catalog loads.

```jsonc
// pipelines/progression-calc.pipeline.json
{
  "key": "progression-calc", "version": 1, "name": "Progression calculation",
  "inputs": [ { "name": "a1", "type": "number" }, { "name": "ratio", "type": "number" },
              { "name": "n", "type": "integer" }, { "name": "coefficients", "type": "list", "itemType": "number" } ],
  "steps": [
    { "key": "terms", "stage": "progression-terms@1" },
    { "key": "polynomial", "stage": "polynomial-eval@1",
      "bindings": { "coefficients": "$pipeline.coefficients", "x": "$steps.terms.terms" } },
    { "key": "statistics", "stage": "statistics@1", "bindings": { "values": "$steps.polynomial.values" } }
  ],
  "outputs": [ { "name": "sum", "source": "$steps.statistics.sum" } ]
}
```

```csharp
await using var result = await runner.RunPipelineAsync("progression-calc",
    new { a1 = 1, ratio = 2, n = 4, coefficients = new[] { 1.0, 0.0, 1.0 } });
double sum = result.GetOutput<double>("sum");
double[] terms = result.GetStepOutput<double[]>("terms", "terms");   // debugging
```

The same pipeline can be defined in C# with `AddPipeline("key", p => p.Input(...).Step(...).Output(...))`. If a step
fails, the following steps end as `Skipped` and the run ends as `Failed`.

## Persistence (PostgreSQL)

With the `PythonScriptRunner.EntityFrameworkCore` package, stages and pipelines live in the database (schema `psr`).
Their versions are immutable and move from draft to published to archived. **Every** run is recorded: steps, attempts,
the tail of stdout/stderr and files (stored in `bytea`, read and written as streams).

```csharp
builder.Services
    .AddPythonScriptRunner()
    .AddEntityFrameworkCore(o =>
    {
        o.UseNpgsql(builder.Configuration.GetConnectionString("psr")!);   // PostgreSQL 16+
        o.Retention.Artifacts = TimeSpan.FromDays(7);                     // files
        o.Retention.Runs = TimeSpan.FromDays(90);                         // history
    });
builder.Services.AddHealthChecks().AddPythonScriptRunnerDatabase();

var app = builder.Build();
await app.Services.MigratePythonScriptRunnerAsync();   // explicit; or GeneratePythonScriptRunnerMigrationScriptAsync()
```

- **Catalog:** `ICatalogAdmin` handles the whole life of a version:
  - creates drafts, with optimistic concurrency;
  - publishes them, after checking the syntax with the declared Python and locking the dependencies with
    `uv pip compile --generate-hashes`;
  - archives, deletes and tests drafts;
  - imports and exports zip files in the folder format.
- **Runs:** `IRunManager` queries runs (keyset pagination), cancels them and retries a failed run. Retried steps that
  succeeded reuse their outputs; the step that failed gets a new attempt. `RunOptions.IdempotencyKey` returns the same
  run for 24 h.
- **Retention:** the history is partitioned (by week for runs, by day for file contents, by month for the audit log).
  A maintenance job creates partitions ahead of time and drops old ones. A pipeline can shorten its retention.
- **Audit:** human actions (`Actor.User(...)`) go to `psr.audit_events`; automatic triggers are recorded on the run
  itself.

## Queue, worker and scheduling (RabbitMQ)

With `PythonScriptRunner.RabbitMQ` and `PythonScriptRunner.Worker`, runs can be queued. Each step is a message:

1. A worker claims the step with a *lease* and runs the Python process.
2. It writes the result, and the message for the next step, in the same transaction (an outbox in PostgreSQL).

Enqueuing works even while the broker is down.

```csharp
builder.Services
    .AddPythonScriptRunner()
    .AddEntityFrameworkCore(o => o.UseNpgsql(builder.Configuration.GetConnectionString("psr")!))
    .AddRabbitMq(o => o.ConnectionString = builder.Configuration.GetConnectionString("rabbitmq"))   // RabbitMQ 3.13+
    .AddWorker(o => o.MaxConcurrency = 8)                         // only in the processes that run steps
    .AddRunEventHandler<NotifyErp>(s => s.Subscription("erp").On("run.succeeded.progression-calc", "run.failed.*"));
builder.Services.AddHealthChecks().AddPythonScriptRunnerChecks();  // database, broker and uv

// ...
RunHandle run = await queue.EnqueuePipelineAsync("progression-calc", new { a1 = 1, ratio = 2, n = 4, coefficients = new[] { 1, 0, 1 } });
```

- **Reliability:**
  - automatic retry per stage, with exponential backoff;
  - a lost worker is detected through its *lease*. The step resumes if the stage is idempotent or has retries left;
    otherwise it fails with `WorkerLost` for a manual retry;
  - duplicate messages never run a step twice.
- **Concurrency:** `MaxConcurrency` per worker and `max_concurrent_runs` per pipeline (the other runs wait as `Pending`,
  in order).
- **Cancellation:** `IRunManager.CancelAsync` stops the process tree on the worker that runs the step.
- **Scheduling:** `ICatalogAdmin.CreateScheduleAsync` takes a cron expression and an IANA time zone (default
  `America/Sao_Paulo`). A missed occurrence becomes one catch-up run, and several workers never fire the same occurrence.
- **External systems:** the public `StartPipelineRun` command (exchange `psr.public`) starts pipelines. The `run.*` and
  `step.*` events (exchange `psr.events`) report progress and outputs. The contracts are in `schemas/messages`.
- **Observability:** `ActivitySource` and `Meter` named `PythonScriptRunner`; the `traceparent` follows the run all the
  way to the worker. The relay polls the outbox every 250 ms, so the library logs its SQL commands at `Debug`
  (`Microsoft.EntityFrameworkCore.Database.Command`).

`hosts/PythonScriptRunner.WorkerHost` is a ready-made worker. It provides `/health/live`, `/health/ready`, JSON logs and
OTLP, and is configured with environment variables:

- `ConnectionStrings__psr` and `ConnectionStrings__rabbitmq`;
- `PythonScriptRunner__Worker__MaxConcurrency`;
- `PythonScriptRunner__Secrets__<NAME>`;
- `PSR_AUTO_MIGRATE=true`.

## Dashboard

With `PythonScriptRunner.Dashboard`, the dashboard is embedded in your application, in the style of Hangfire. It has an
internal API, real-time updates (SignalR) and the Vue UI, served under the prefix you choose. Without
`MapPythonScriptRunnerDashboard` there is no dashboard.

The UI is in Brazilian Portuguese, with light and dark themes. It covers:

- an overview;
- **Stages**: a list you can filter by pipeline, a Monaco editor with a field builder, test runs through the queue, and
  publishing;
- **Pipelines**: a visual editor with typed *handles* or an editable list, test runs, and publishing;
- live runs: the flow by status, logs, files, a timeline, cancel and retry;
- schedules with a cron preview;
- the audit log and administration.

The UI ships inside the package: there is nothing to host separately.

```csharp
builder.Services
    .AddPythonScriptRunner()
    .AddEntityFrameworkCore(o => o.UseNpgsql(builder.Configuration.GetConnectionString("psr")!))
    .AddRabbitMq(o => o.ConnectionString = builder.Configuration.GetConnectionString("rabbitmq"))
    .AddDashboard(o => o.UseFixedLogin(login => login.BindConfiguration()));   // section PythonScriptRunner:Dashboard:Login

var app = builder.Build();
app.MapPythonScriptRunnerDashboard("/psr");   // API at /psr/api, real time at /psr/hubs/runs, UI at /psr/
```

Choose **one** access mode. Roles are cumulative:

1. `Reader` sees everything.
2. `Executor` also triggers, cancels and retries runs.
3. `Author` also edits, tests, publishes, imports and schedules.
4. `Admin` also deletes and manages retention, secrets and the audit log.

| Mode | Configuration |
|------|---------------|
| Fixed login | `o.UseFixedLogin(l => l.BindConfiguration())` with users in the configuration (below) or `l.Users` in code |
| Custom filter | `o.UseAuthorizationFilter<MyFilter>()`: an `IDashboardAuthorizationFilter` returns `DashboardUser(name, role)` or `null` |
| Host authentication | `o.UseHostAuthentication(r => { r.Reader = p => p.RequireRole("psr-reader"); /* Executor, Author, Admin */ r.LoginUrl = "/account/login?returnUrl={returnUrl}"; })` |
| Local only | `o.AllowLocalRequests = true`: Development only (outside it, startup fails); it adds to a main mode |

```json
"PythonScriptRunner": {
  "Dashboard": {
    "Login": {
      "Users": [
        { "Name": "ana", "Role": "Admin", "PasswordHash": "AQAAAAIAAYagAAAAE..." },
        { "Name": "bruno", "Role": "Reader", "PasswordHash": "AQAAAAIAAYagAAAAE..." }
      ]
    }
  }
}
```

The password never goes into the configuration, only its hash (PBKDF2, in the ASP.NET Identity format). The `psr` tool
generates it:

```bash
dotnet tool install -g PythonScriptRunner.Tool
psr hash-password                                # type the password twice, without echo
echo "password" | psr hash-password --stdin      # in scripts
```

How fixed login behaves:

- **Session:** a `psr.session` cookie (HttpOnly, `SameSite=Lax`), sliding for 8 h, 24 h at most.
- **Lockout:** 5 failures in a row lock the user for 15 min; one IP can try 10 times per minute.
- **Configuration changes** apply without a restart. A removed user or a changed password ends the session; a new role
  applies immediately.
- **Cookie keys** live in `psr.dashboard_keys`, so a session works on every instance and survives restarts.

In the API, requests that change something require the `X-PSR-Request: 1` header. Edits use `ETag`/`If-Match`, and
errors follow `ProblemDetails` with a `code`. In Development, the (internal) OpenAPI document is at
`/psr/api/openapi/v1.json`.

`hosts/PythonScriptRunner.DashboardHost` is a ready-made dashboard. It authenticates in one of two ways:

- **Fixed login**, with users from environment variables (`PythonScriptRunner__Dashboard__Login__Users__0__Name`,
  `__Role`, `__PasswordHash`).
- **OpenID Connect**, with `Oidc__Authority`, `Oidc__ClientId` and `Oidc__ClientSecret`. Roles come from the
  `Dashboard__RoleClaim` claim (default `roles`), mapped with `Dashboard__Roles__Reader|Executor|Author|Admin`.

It also accepts these variables:

- `Dashboard__PathPrefix` (default `/psr`);
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` behind a proxy;
- `PSR_AUTO_MIGRATE=true`.

## Packages

| Package | Contents |
|---------|----------|
| `PythonScriptRunner.Abstractions` | public contracts: field types, `StageDefinition`, `RunResult`, `PsrFile`, interfaces |
| `PythonScriptRunner.Core` | the runner: catalogs (code and folder), validation, uv environments, process executor |
| `PythonScriptRunner.EntityFrameworkCore` | PostgreSQL persistence: versioned catalog, history, files, audit, retention |
| `PythonScriptRunner.RabbitMQ` | queue, outbox relay, public command and events |
| `PythonScriptRunner.Worker` | runs queued steps; cron scheduling |
| `PythonScriptRunner.Dashboard` | embeddable dashboard (API, SignalR, Vue UI) |
| `PythonScriptRunner.Tool` | the `psr` dotnet tool (`psr hash-password`) |

```bash
dotnet add package PythonScriptRunner.Core
dotnet add package PythonScriptRunner.EntityFrameworkCore   # optional: PostgreSQL
dotnet add package PythonScriptRunner.RabbitMQ              # optional: queue
dotnet add package PythonScriptRunner.Worker                # optional: worker and cron
dotnet add package PythonScriptRunner.Dashboard             # optional: dashboard
```

All 7 packages share one version and ship symbols with SourceLink. The runner installs the `psrunner` SDK from PyPI in
every Python environment it creates (`PythonScriptRunner:PsrunnerRequirement`, default `psrunner==0.1.0`). To use a
private index, set `PythonScriptRunner:PackageIndexUrl` and `PythonScriptRunner:ExtraIndexUrls`.

## Containers

The hosts are published as multi-arch images (amd64 and arm64), with the same version as the NuGet packages, plus
`latest`:

| Image | Host |
|-------|------|
| `ghcr.io/scriptrunnerkit/psr-worker` | `WorkerHost` |
| `ghcr.io/scriptrunnerkit/psr-dashboard` | `DashboardHost` (the Vue UI embedded) |

Both images share these properties:

- They are built on `mcr.microsoft.com/dotnet/aspnet:10.0` with `uv`.
- They run as the non-root user `psr` (uid 10001) and listen on port 8080, with `/health/live` and `/health/ready`.
- They include the `psrunner` wheel of the same commit: the Python environments do not download the SDK. The stage
  dependencies still come from PyPI or from your index.
- They carry an SBOM and provenance attestations.

```bash
docker run -d --read-only \
  --tmpfs /tmp/psr:uid=10001,gid=10001,exec \
  -v psr-envs:/var/lib/psr/envs -v psr-python:/var/lib/psr/python \
  -e ConnectionStrings__psr="Host=pg;Database=psr;Username=psr_app;Password=..." \
  -e ConnectionStrings__rabbitmq="amqp://psr:...@rabbit:5672" \
  ghcr.io/scriptrunnerkit/psr-worker:0.1.0
```

| Variable | Host | Example |
|----------|------|---------|
| `ConnectionStrings__psr` | both | `Host=pg;Database=psr;Username=psr_app;Password=…` |
| `ConnectionStrings__rabbitmq` | both | `amqps://psr:…@rabbit:5671/psr` |
| `PSR_AUTO_MIGRATE` | both | `true` applies the migrations at startup (default `false`); concurrent hosts wait on an advisory lock |
| `PythonScriptRunner__Worker__MaxConcurrency` | worker | `8` |
| `PythonScriptRunner__Secrets__<NAME>` | worker | from the platform's secret store |
| `PythonScriptRunner__ThreadsPerProcess` | both | threads for the numeric libraries (see [Memory limits](#memory-limits)) |
| `PythonScriptRunner__PackageIndexUrl` | both | private package index |
| `PythonScriptRunner__Dashboard__Login__Users__N__Name` / `__Role` / `__PasswordHash` | dashboard | fixed-login users |
| `Oidc__Authority`, `Oidc__ClientId`, `Oidc__ClientSecret` | dashboard | optional: turns on OpenID Connect |
| `Dashboard__PathPrefix` | dashboard | `/psr` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | both | `http://otel-collector:4317` |
| `Serilog__MinimumLevel__Override__<category>` | both | `Information` for `Microsoft.AspNetCore` brings back the request logs (default `Warning`) |

### Volumes and read-only file system

The images run with a read-only root file system. Python writes only to these volumes:

| Path | Contents | If it is not persistent |
|------|----------|-------------------------|
| `/var/lib/psr/envs` | cached virtual environments (one per set of dependencies) and the uv cache | every start recreates the environments, so the first run of each stage is slower |
| `/var/lib/psr/python` | Python interpreters installed by uv | the interpreter is downloaded again |
| `/tmp/psr` | work folders of the runs and `TMPDIR` | expected: it only holds temporary files |

A tmpfs works for any of them, but it must allow execution (`exec`), because the volumes run Python; Docker mounts tmpfs
`noexec` by default. Without access to PyPI, the environments fail with `EnvironmentSetupFailed`. In that case, point
`PythonScriptRunner__PackageIndexUrl` to a private index, or build the image with an interpreter already installed:

```bash
docker build --build-arg PSR_PREINSTALL_PYTHON=3.12 -f hosts/PythonScriptRunner.WorkerHost/Dockerfile -t my-psr-worker .
```

### Memory limits

Every Python process gets the memory limit of its stage (default 1024 MB). How it is enforced depends on the platform,
and the host logs the mode it uses at startup:

- **Windows:** a Job Object per process.
- **Linux with a delegable cgroup v2:** each process runs in its own cgroup (`memory.max`, no swap). When the process
  exceeds it, the kernel kills it and the attempt ends with `MemoryLimitExceeded` (code `OUT_OF_MEMORY`). The host needs
  write access to its own cgroup: a systemd service with `Delegate=yes`, or a container running as root with a writable
  cgroup file system. Log: `Memory limits of Python processes use cgroup v2 under …`.
- **Other Linux setups, including standard containers** (they mount the cgroup file system read-only): an address-space
  limit (`RLIMIT_AS`). Numeric libraries (OpenBLAS, MKL, OpenMP, NumExpr, vecLib) reserve address space per thread, so in
  this mode they get 1 thread per process. Log: `Memory limits of Python processes use RLIMIT_AS …`.

`PythonScriptRunner:ThreadsPerProcess` sets the thread count in every mode. Raise it for parallel numeric work, with a
memory limit that has room for it.

### Kubernetes

There is no official Helm chart yet. This example of a worker `Deployment` shows the settings that matter:

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: psr-worker
spec:
  replicas: 2
  selector:
    matchLabels: { app: psr-worker }
  template:
    metadata:
      labels: { app: psr-worker }
    spec:
      # Longer than PythonScriptRunner__Worker__ShutdownGracePeriod (default 5 min), so running steps can finish.
      terminationGracePeriodSeconds: 330
      securityContext:
        runAsNonRoot: true
        runAsUser: 10001
        fsGroup: 10001
      containers:
        - name: worker
          image: ghcr.io/scriptrunnerkit/psr-worker:0.1.0
          env:
            - name: ConnectionStrings__psr
              valueFrom: { secretKeyRef: { name: psr, key: postgres } }
            - name: ConnectionStrings__rabbitmq
              valueFrom: { secretKeyRef: { name: psr, key: rabbitmq } }
          ports: [ { containerPort: 8080 } ]
          livenessProbe: { httpGet: { path: /health/live, port: 8080 } }
          readinessProbe: { httpGet: { path: /health/ready, port: 8080 } }
          securityContext:
            readOnlyRootFilesystem: true
            allowPrivilegeEscalation: false
          resources:
            limits: { memory: 4Gi }   # room for MaxConcurrency processes at their stage limit
          volumeMounts:
            - { name: envs, mountPath: /var/lib/psr/envs }
            - { name: python, mountPath: /var/lib/psr/python }
            - { name: work, mountPath: /tmp/psr }
      volumes:
        - { name: envs, emptyDir: {} }     # a PersistentVolumeClaim keeps the environments across restarts
        - { name: python, emptyDir: {} }
        - { name: work, emptyDir: {} }
```

If `terminationGracePeriodSeconds` is shorter than `ShutdownGracePeriod`, the kubelet kills attempts that are still
running. They then follow the lease rules: they resume on another worker or fail with `WorkerLost`.

## Local environment (.NET Aspire)

With Docker, the .NET 10 SDK, Node.js 24 and uv installed:

```bash
dotnet run --project hosts/PythonScriptRunner.AppHost
```

The AppHost starts PostgreSQL 16, RabbitMQ 3.13 (with the management UI), Mailpit (a test SMTP server), the WorkerHost,
the DashboardHost and the Vue UI in development mode (Vite, with hot reload). The Aspire dashboard
(`http://localhost:15000`) shows the resources, logs and traces of every run. The link **Painel** on the `dashboard`
resource opens the UI.

The DashboardHost has one development user per role, all with the password `psr-dev`:

| User | Role |
|------|------|
| `leitor` | Reader |
| `executor` | Executor |
| `autor` | Author |
| `admin` | Admin |

The sample pipelines are in `tests/fixtures`. To import one:

1. Zip the contents of its folder, with `stages/` and `pipelines/` at the root of the zip.
2. Import it in the UI as `autor`.
3. Publish the stages, then the pipeline.

`html-para-email` sends its e-mail to Mailpit.

## Docker Compose

`aspire publish` turns the AppHost into a `docker-compose.yaml` that uses the published images:

```bash
dotnet tool install --global Aspire.Cli --version 13.6.2
aspire publish --apphost hosts/PythonScriptRunner.AppHost/PythonScriptRunner.AppHost.csproj -o deploy
```

Fill in `deploy/.env`, then start it with `docker compose up -d` in `deploy`:

| Variable | Meaning |
|----------|---------|
| `POSTGRES_PASSWORD` | password of the `postgres` user |
| `RABBITMQ_PASSWORD` | password of the RabbitMQ `guest` user |
| `PSR_VERSION` | image version (default `latest`) |
| `PSR_DASHBOARD_PORT` | port of the dashboard on the host (default `8080`); the UI is at `/psr/` |

The compose includes an Aspire dashboard for the traces. It is a starting point, and it keeps the development setup:

- the four users with the password `psr-dev`;
- Mailpit;
- the test SMTP secrets.

Before you expose it, replace the `PasswordHash` values with hashes from `psr hash-password` (or configure OpenID
Connect), and remove what you do not use.

## Compatibility

| PythonScriptRunner (NuGet and images) | .NET | Protocol | `psrunner` | Python |
|---------------------------------------|------|----------|------------|--------|
| 0.1.x | 10 | 1 | 0.1.x | 3.10–3.13 |

The `psrunner` SDK has its own version on PyPI. It stays compatible with protocol 1 for as long as protocol 1 is in use.
The infrastructure needs PostgreSQL 16+ and RabbitMQ 3.13+.

## Requirements

- .NET 10 SDK.
- [uv](https://docs.astral.sh/uv/) on the PATH. It creates and caches one Python environment per set of dependencies.
- Docker, only for the tests and the local environment: the tests use Testcontainers.
- PostgreSQL 16+, only for the persistence package.
- RabbitMQ 3.13+, only for the queue.
- Node.js 24, only to build in Release or to pack `PythonScriptRunner.Dashboard`: the build embeds the UI from `panel/`.

## Repository layout

| Folder | Contents |
|--------|----------|
| `src/PythonScriptRunner.Abstractions` | public contracts (field types, `StageDefinition`, `RunResult`, `PsrFile`, interfaces) |
| `src/PythonScriptRunner.Core` | catalogs (code and folder), validation, uv environments, process executor, runner |
| `src/PythonScriptRunner.EntityFrameworkCore` | PostgreSQL: versioned catalog, history, files, idempotency, outbox, audit, retention, schedules and the lifecycle of queued runs |
| `src/PythonScriptRunner.RabbitMQ` | topology, outbox relay, `IRunQueue`, public command, events and subscriptions |
| `src/PythonScriptRunner.Worker` | runs queued steps: *lease*, retry, cancellation, cron |
| `src/PythonScriptRunner.Dashboard` | embeddable dashboard: internal API, SignalR hub, access modes (fixed login, filter, host, local) |
| `src/PythonScriptRunner.Tool` | the `psr` dotnet tool (`psr hash-password`) |
| `panel` | the dashboard UI: Vue 3, Vuetify 4, Vue Flow and Monaco (TypeScript, Vite, Vitest) |
| `hosts` | `WorkerHost` and `DashboardHost` (with their Dockerfiles), `ServiceDefaults` (Serilog, OpenTelemetry, health checks) and the Aspire `AppHost` |
| `python/psrunner` | the Python SDK (`@stage`, `Context`, `File`, `StageError`, `psrunner.testing`, CLI) |
| `schemas` | JSON Schemas for `stage.json`, `*.pipeline.json` and the public messages |
| `tests` | .NET tests (unit, integration with real Python, containers, Aspire and dashboard E2E with Playwright) and sample pipelines |

## Development

```bash
dotnet build
dotnet test --project tests/PythonScriptRunner.Core.Tests
dotnet test --project tests/PythonScriptRunner.EntityFrameworkCore.Tests   # needs Docker
dotnet test --project tests/PythonScriptRunner.RabbitMQ.Tests              # needs Docker
dotnet test --project tests/PythonScriptRunner.Worker.Tests                # needs Docker
dotnet test --project tests/PythonScriptRunner.Dashboard.Tests             # needs Docker
dotnet test --project tests/PythonScriptRunner.AppHost.Tests               # needs Docker, Node.js and uv

cd python/psrunner
uv sync
uv run pytest
```

The UI lives in `panel/` (Node 24). Debug builds embed a placeholder page. In Release (or with `-p:PsrBuildPanel=true`),
MSBuild runs `npm ci` and `npm run build` and embeds `panel/dist`.

```bash
cd panel
npm ci
npm run dev        # Vite at http://localhost:5173/; /api and /hubs go to the DashboardHost at PSR_DASHBOARD_URL (default http://localhost:5080, prefix /psr)
npm test           # Vitest
npm run lint       # ESLint + Prettier
npm run build      # type check, build and the budget of the initial bundle (≤ 500 KB gzip)
npm run api        # regenerates src/api/schema.d.ts from the OpenAPI snapshot
```

The dashboard E2E tests need Docker and the embedded UI. They start the `DashboardHost` with 4 fixed-login users, a
worker, PostgreSQL and RabbitMQ, and drive Chromium with Playwright:

```bash
dotnet build tests/PythonScriptRunner.Panel.E2E.Tests -p:PsrBuildPanel=true
dotnet test --project tests/PythonScriptRunner.Panel.E2E.Tests --no-build   # PSR_E2E_HEADED=1 shows the browser
```

When a test fails, the Playwright trace is saved to `TestResults/traces` next to the test assembly
(`npx playwright show-trace <file>.zip`).

Run the E2E tests on their own. Containers starting or stopping in other test projects change the network interfaces,
and Chromium then aborts requests with `ERR_NETWORK_CHANGED`. The solution filter runs the unit, integration and
dashboard tests at once (the CI does the same; the container and AppHost tests run in their own jobs):

```bash
dotnet test --solution tests/PythonScriptRunner.SemE2E.slnf
```

The container tests build both images and run them the way production does: read-only, with tmpfs volumes. They cover
the memory limit through cgroup v2 and the compose from `aspire publish`; the compose test needs the Aspire CLI
(`PSR_ASPIRE_CLI` points to it if it is not on the PATH). To test images you built yourself, set
`PSR_TEST_WORKER_IMAGE` and `PSR_TEST_DASHBOARD_IMAGE`:

```bash
dotnet test --project tests/PythonScriptRunner.Containers.Tests   # needs Docker
```

When a container test fails, it saves the logs of its containers to `TestResults/container-logs/<test>/` next to the test
assembly, and the end of the worker and dashboard logs goes to the test output. The CI uploads that folder as the
`container-logs` artifact.

The dashboard performance test uses about 200,000 runs; `PSR_PERF_RUNS_PER_DAY=50000` runs the full average volume (90
days). The dashboard's OpenAPI document is compared with
`tests/PythonScriptRunner.Dashboard.Tests/Snapshots/openapi-v1.verified.json`. After you review an intended change,
`PSR_ACCEPT_SNAPSHOTS=1` updates the snapshot.

## Contributing and security

- [CONTRIBUTING.md](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/CONTRIBUTING.md): how to build, test and send changes.
- [CHANGELOG.md](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/CHANGELOG.md): the history of each version.
- [SECURITY.md](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/SECURITY.md): how to report a vulnerability privately.

## License

[MIT](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/LICENSE)
