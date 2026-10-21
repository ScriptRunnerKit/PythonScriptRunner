# PythonScriptRunner

[![CI](https://github.com/ScriptRunnerKit/PythonScriptRunner/actions/workflows/ci.yml/badge.svg)](https://github.com/ScriptRunnerKit/PythonScriptRunner/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/PythonScriptRunner.Core?label=NuGet)](https://www.nuget.org/packages/PythonScriptRunner.Core)
[![PyPI](https://img.shields.io/pypi/v/psrunner?label=PyPI)](https://pypi.org/project/psrunner/)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/LICENSE)

Run Python from .NET with a typed contract. The calculation stays in a plain Python script (numpy, pandas, scipy,
anything on PyPI), written by whoever knows the subject. The .NET application receives a result that was already validated
against the contract.

Start with the library alone, with no database and no message broker. Add PostgreSQL for history and versions, RabbitMQ
for background workers and cron schedules, and an embedded dashboard to edit, test and follow everything in the browser.

**Documentation:** [guides in English](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/en/README.md) |
[guias em português](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/pt-br/README.md)

![A pipeline run in the dashboard](https://raw.githubusercontent.com/ScriptRunnerKit/PythonScriptRunner/main/guides/images/dashboard-run-live.png)

## Why

Teams that do a lot of calculation often meet the same problem: the math lives in Python, but the system that needs the
result is .NET. The usual ways out are rewriting the formula in C#, or calling `python` with `Process.Start` and parsing
an unchecked JSON. PythonScriptRunner gives the two sides a contract instead:

- **Typed contract:** inputs and outputs have types (12 of them, including `decimal`, `date`, `table`, `file` and lists),
  checked on both sides.
- **Isolated processes:** each run gets its own process, with a timeout, a memory limit and its own Python environment
  created by uv.
- **Reproducible dependencies:** published versions lock their requirements with hashes.
- **Typed results:** the .NET side gets typed values or a clear error, never invalid data.

## Ways to use it

| Level | What you need | What you get | Guide |
|-------|---------------|--------------|-------|
| **1. Library only** | .NET 10 and uv | Run stages and pipelines from your code. No database, no broker, no dashboard. | [Getting started](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/en/getting-started.md) |
| **2. With PostgreSQL** | PostgreSQL 16+ | A versioned catalog, the history of every run, files, retention and audit. | [Persistence](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/en/persistence.md) |
| **3. With RabbitMQ** | PostgreSQL and RabbitMQ 3.13+ | Background runs on workers, retry, cron schedules, events, and a command for systems in other languages. | [Queue, worker and scheduling](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/en/queue-and-worker.md) |
| **4. With the dashboard** | PostgreSQL and RabbitMQ | An embedded web UI to edit, test, run and follow stages and pipelines. | [Dashboard](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/en/dashboard.md) |
| **Ready-made hosts** | Docker | Worker and dashboard container images, or the complete environment with one command on .NET Aspire. | [Hosting](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/en/hosting.md) |

## Quick start

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and [uv](https://docs.astral.sh/uv/)
(`winget install --id=astral-sh.uv -e` on Windows, `curl -LsSf https://astral.sh/uv/install.sh | sh` on macOS and Linux).
You do not need to install Python: uv downloads the version each stage asks for.

```bash
dotnet new console -n MyCalculation && cd MyCalculation
dotnet add package PythonScriptRunner.Core
dotnet add package Microsoft.Extensions.Hosting
```

A stage is a folder with a contract and a script. `psr/stages/average/stage.json`:

```json
{
  "key": "average",
  "version": 1,
  "name": "Average",
  "inputs":  [ { "name": "values", "type": "list", "itemType": "number" } ],
  "outputs": [ { "name": "average", "type": "number" } ]
}
```

`psr/stages/average/script.py`:

```python
from psrunner import stage

@stage
def run(inputs, ctx):
    values = inputs["values"]
    ctx.log.info("averaging %d values", len(values))
    return {"average": sum(values) / len(values)}
```

`Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PythonScriptRunner;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPythonScriptRunner().AddFileCatalog("psr");

using var host = builder.Build();
await host.StartAsync();

var runner = host.Services.GetRequiredService<IPythonRunner>();
await using var result = await runner.RunStageAsync("average", new { values = new[] { 2.0, 4.0, 9.0 } });
result.EnsureSuccess();
Console.WriteLine(result.GetOutput<double>("average"));   // 5
```

Add a `requirements.txt` next to the script to use packages such as numpy or pandas. More in
[Getting started](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/en/getting-started.md) and
[Stages](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/en/stages.md).

## Stages and the contract

A stage declares its inputs and outputs with one of 12 types: `number`, `integer`, `decimal`, `string`, `bool`, `date`,
`datetime`, `enum`, `json`, `table`, `file` and `list<T>`. Values are converted on both sides. For example, `decimal`
becomes `decimal` in .NET and `Decimal` in Python, and `table` becomes a list of records or a `pandas.DataFrame`.

Each stage can also declare:
- its Python version and requirements;
- a timeout and a memory limit;
- an automatic retry policy;
- the secrets it receives.

Stages can be defined in `stage.json` files or in C#
(`AddStage("key", s => s.Script(...).Input(...).Output(...))`). See
[Stages](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/en/stages.md).

## Pipelines

A pipeline runs stages in order. Each step input comes from a pipeline input (`$pipeline.field`), from an output of any
earlier step (`$steps.step.field`) or from a constant, and types are checked before anything runs.

```csharp
await using var result = await runner.RunPipelineAsync("progression-calc",
    new { a1 = 1, ratio = 2, n = 4, coefficients = new[] { 1.0, 0.0, 1.0 } });
double sum = result.GetOutput<double>("sum");
```

If a step fails, the following ones are skipped and the error says which step broke. See
[Pipelines](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/en/pipelines.md).

## Python SDK

The [`psrunner`](https://pypi.org/project/psrunner/) package is the Python side. The runner installs it in every
environment; script authors install it only to test locally, with pytest and without .NET:

```python
from psrunner.testing import run_stage

def test_average():
    result = run_stage("psr/stages/average/script.py", {"values": [2, 4, 9]},
                       stage_json="psr/stages/average/stage.json")
    assert result.outputs == {"average": 5.0}
```

Scripts also get a context with:
- a logger;
- the declared secrets;
- output files.

Business errors are raised with `StageError(message, code=...)`. See the
[Python SDK guide](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/en/python-sdk.md).

## Persistence (PostgreSQL)

```csharp
builder.Services.AddPythonScriptRunner()
    .AddEntityFrameworkCore(o => o.UseNpgsql(builder.Configuration.GetConnectionString("psr")!));
await app.Services.MigratePythonScriptRunnerAsync();
```

- **Versions:** stages and pipelines have immutable versions (draft, published, archived). Publishing locks the
  requirements with hashes.
- **Runs:** every run is recorded, with its steps, attempts, logs and files.
- **Management:** `IRunManager` queries runs, cancels them and retries a failed run from the step that failed.
- **History:** retention is partitioned and cleaned by a maintenance job. Human actions go to an audit log.

See [Persistence](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/en/persistence.md).

## Queue, worker and scheduling (RabbitMQ)

```csharp
builder.Services.AddPythonScriptRunner()
    .AddEntityFrameworkCore(o => o.UseNpgsql(builder.Configuration.GetConnectionString("psr")!))
    .AddRabbitMq(o => o.ConnectionString = builder.Configuration.GetConnectionString("rabbitmq"))
    .AddWorker(o => o.MaxConcurrency = 4);

RunHandle run = await queue.EnqueuePipelineAsync("progression-calc", inputs);
```

- **Transactional outbox:** enqueuing works even while the broker is down.
- **Retry:** failed attempts are retried with exponential backoff.
- **Lost workers:** a lost worker is detected by its lease and the step resumes elsewhere.
- **Cron schedules:** they run in any IANA time zone, and several workers never fire the same occurrence.
- **Other languages:** systems written in other languages start pipelines with a `StartPipelineRun` message and follow
  them through `run.*` and `step.*` events.

See [Queue, worker and scheduling](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/en/queue-and-worker.md).

## Dashboard

```csharp
builder.Services.AddPythonScriptRunner()
    .AddEntityFrameworkCore(o => o.UseNpgsql(builder.Configuration.GetConnectionString("psr")!))
    .AddRabbitMq(o => o.ConnectionString = builder.Configuration.GetConnectionString("rabbitmq"))
    .AddDashboard(o => o.UseFixedLogin(login => login.BindConfiguration()));

app.MapPythonScriptRunnerDashboard("/psr");
```

An embedded web UI, in the style of Hangfire:
- a Monaco editor for scripts and a builder for the contract;
- a visual pipeline editor;
- runs started from generated forms and followed live;
- logs, files and a timeline;
- cron schedules, audit and administration.

Access is by fixed login (password hashes from the `psr` tool), by your application's own authentication (including
OpenID Connect), or by a custom filter, with four cumulative roles. See
[Dashboard](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/en/dashboard.md).

![Stage editor](https://raw.githubusercontent.com/ScriptRunnerKit/PythonScriptRunner/main/guides/images/dashboard-stage-editor.png)

## Containers and .NET Aspire

| Image | Host |
|-------|------|
| `ghcr.io/scriptrunnerkit/psr-worker` | Runs queued steps and schedules |
| `ghcr.io/scriptrunnerkit/psr-dashboard` | The dashboard, with the UI embedded |

The images are:
- multi-arch (amd64 and arm64);
- non-root, and they run on a read-only root file system;
- configured by environment variables;
- shipped with an SBOM and provenance attestations.

For a complete local environment (PostgreSQL, RabbitMQ, worker, dashboard and a test SMTP server) with traces in the
Aspire dashboard:

```bash
git clone https://github.com/ScriptRunnerKit/PythonScriptRunner && cd PythonScriptRunner
dotnet run --project hosts/PythonScriptRunner.AppHost
```

`aspire publish` turns the same setup into a Docker Compose file. See
[Hosting](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/en/hosting.md), which also covers
volumes, memory limits and a Kubernetes example.

## Observability and security

- **Observability:** OpenTelemetry traces and metrics (`PythonScriptRunner` source and meter), health checks for the
  database, the broker and uv, and structured logs. One trace follows a run from the HTTP request to every Python
  process.
- **Security:** stages are code from trusted authors and run with the worker's permissions; it is not a sandbox for
  untrusted code. Secrets reach only the stage that declares them and are masked in the logs.

See [How it works](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/en/how-it-works.md) and the
[configuration reference](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/en/configuration.md).

## Packages

| Package | Contents |
|---------|----------|
| [`PythonScriptRunner.Core`](https://www.nuget.org/packages/PythonScriptRunner.Core) | The runner: catalogs (folder and code), validation, uv environments, process executor |
| [`PythonScriptRunner.Abstractions`](https://www.nuget.org/packages/PythonScriptRunner.Abstractions) | Public contracts: field types, results, files, interfaces |
| [`PythonScriptRunner.EntityFrameworkCore`](https://www.nuget.org/packages/PythonScriptRunner.EntityFrameworkCore) | PostgreSQL persistence: versioned catalog, history, files, audit, retention |
| [`PythonScriptRunner.RabbitMQ`](https://www.nuget.org/packages/PythonScriptRunner.RabbitMQ) | Queue, outbox relay, public command and events |
| [`PythonScriptRunner.Worker`](https://www.nuget.org/packages/PythonScriptRunner.Worker) | Runs queued steps and cron schedules |
| [`PythonScriptRunner.Dashboard`](https://www.nuget.org/packages/PythonScriptRunner.Dashboard) | Embedded dashboard (API, SignalR, Vue UI) |
| [`PythonScriptRunner.Tool`](https://www.nuget.org/packages/PythonScriptRunner.Tool) | The `psr` dotnet tool (`psr hash-password`) |
| [`psrunner`](https://pypi.org/project/psrunner/) (PyPI) | The Python SDK |

All NuGet packages share one version and ship symbols with SourceLink.

## Compatibility

| PythonScriptRunner (NuGet and images) | .NET | Protocol | `psrunner` | Python |
|---------------------------------------|------|----------|------------|--------|
| 0.1.x | 10 | 1 | 0.1.x | 3.10 to 3.13 |

The `psrunner` SDK has its own version on PyPI and stays compatible with protocol 1 for as long as that protocol is in
use. The infrastructure needs PostgreSQL 16+ and RabbitMQ 3.13+, only at the levels that use them.

## Repository layout

| Folder | Contents |
|--------|----------|
| `src/` | The NuGet packages (Abstractions, Core, EntityFrameworkCore, RabbitMQ, Worker, Dashboard, Tool) |
| `panel/` | The dashboard UI: Vue 3, Vuetify 4, Vue Flow and Monaco (TypeScript, Vite, Vitest) |
| `hosts/` | `WorkerHost` and `DashboardHost` (with their Dockerfiles), `ServiceDefaults` and the Aspire `AppHost` |
| `python/psrunner/` | The Python SDK |
| `schemas/` | JSON Schemas of `stage.json`, `*.pipeline.json` and the public messages |
| `guides/` | The documentation, in English and Portuguese |
| `tests/` | .NET tests (unit, integration with real Python, PostgreSQL and RabbitMQ, containers, Aspire and dashboard end-to-end with Playwright) and sample pipelines |

## Development

```bash
dotnet build
dotnet test --solution tests/PythonScriptRunner.SemE2E.slnf   # unit, integration and dashboard tests (needs Docker)

cd python/psrunner && uv sync && uv run pytest
cd panel && npm ci && npm test
```

[CONTRIBUTING.md](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/CONTRIBUTING.md) covers the
prerequisites, every test project, the checks the CI runs and how releases work.

## Contributing and security

- [CONTRIBUTING.md](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/CONTRIBUTING.md): how to build, test and send changes.
- [CHANGELOG.md](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/CHANGELOG.md): the history of each version.
- [SECURITY.md](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/SECURITY.md): how to report a vulnerability privately.

## License

[MIT](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/LICENSE)
