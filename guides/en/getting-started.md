# Getting started

English | [Português](../pt-br/getting-started.md)

PythonScriptRunner connects Python code to .NET applications. The calculation stays in a plain Python script, written by
whoever knows the subject, and the .NET side receives a result that was already validated against a contract.

## Concepts

- **Stage**: one Python script with a **contract**, the list of its inputs and outputs and their types. The script
  implements a single function, `run(inputs, ctx)`; the library validates what goes in and what comes out.
- **Pipeline**: an ordered sequence of stages. The output of one step can feed the input of a later one, and the types of
  every connection are checked before anything runs.
- **Run**: one execution of a stage or pipeline. Each attempt runs in its own Python process, with a timeout, a memory
  limit and its own Python environment.

## Ways to use it

Start with the smallest setup that does the job. The code of one level keeps working at the next one.

| Level | What you need | What you get |
|-------|---------------|--------------|
| **1. Library only** | .NET 10 and uv | Run stages and pipelines from your own code. No database, no message broker, no dashboard. |
| **2. With PostgreSQL** | PostgreSQL 16 or newer | A versioned catalog (draft, published, archived), the history of every run, files and an audit log. See [Persistence](persistence.md). |
| **3. With RabbitMQ** | PostgreSQL and RabbitMQ 3.13 or newer | Background runs on workers, automatic retry, cron schedules, events and a command for systems written in other languages. See [Queue, worker and scheduling](queue-and-worker.md). |
| **4. With the dashboard** | PostgreSQL and RabbitMQ | A web UI embedded in your application to edit, test, run and follow stages and pipelines. See [Dashboard](dashboard.md). |
| **Ready-made hosts** | Docker | Worker and dashboard container images, or the complete environment with one command (.NET Aspire). See [Hosting](hosting.md). |

## Prerequisites

1. **.NET 10 SDK**: <https://dotnet.microsoft.com/download>
2. **uv**, which creates and caches the Python environments. You do not need to install Python: uv downloads the version
   each stage asks for.
   - Windows: `winget install --id=astral-sh.uv -e`
   - macOS and Linux: `curl -LsSf https://astral.sh/uv/install.sh | sh`

## Packages

| Package | Use |
|---------|-----|
| `PythonScriptRunner.Core` | Run stages and pipelines (level 1). Brings `PythonScriptRunner.Abstractions`. |
| `PythonScriptRunner.EntityFrameworkCore` | PostgreSQL persistence (level 2) |
| `PythonScriptRunner.RabbitMQ` | Queue, outbox relay, public command and events (level 3) |
| `PythonScriptRunner.Worker` | Runs queued steps and cron schedules (level 3) |
| `PythonScriptRunner.Dashboard` | Embedded web UI (level 4) |
| `PythonScriptRunner.Tool` | The `psr` command line tool (`psr hash-password` for the dashboard login) |
| `psrunner` (PyPI) | The Python SDK. The runner installs it in every environment; install it yourself only to test scripts locally. |

All NuGet packages share one version. See the [compatibility table](../../README.md#compatibility).

## Your first stage

Create a console project:

```bash
dotnet new console -n MyCalculation
cd MyCalculation
dotnet add package PythonScriptRunner.Core
dotnet add package Microsoft.Extensions.Hosting
```

Create a folder `psr/stages/average` with two files.

`psr/stages/average/stage.json`, the contract:

```json
{
  "key": "average",
  "version": 1,
  "name": "Average",
  "inputs":  [ { "name": "values", "type": "list", "itemType": "number" } ],
  "outputs": [ { "name": "average", "type": "number" } ]
}
```

`psr/stages/average/script.py`, the code:

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
builder.Services
    .AddPythonScriptRunner()
    .AddFileCatalog("psr");                 // folder with stages/ and pipelines/

using var host = builder.Build();
await host.StartAsync();                    // checks the configuration (uv on the PATH, the catalog)

var runner = host.Services.GetRequiredService<IPythonRunner>();
await using var result = await runner.RunStageAsync("average", new { values = new[] { 2.0, 4.0, 9.0 } });
result.EnsureSuccess();
Console.WriteLine(result.GetOutput<double>("average"));   // 5
```

Run it with `dotnet run`. The first run takes a few seconds longer because uv creates the Python environment; later runs
reuse it.

What happened:
- The inputs were checked against the contract before Python started.
- The script ran in a separate process, in a clean environment created by uv.
- The output was checked against the contract and converted to `double`.

If the script returns something outside the contract (a text where a number is expected, or `NaN`), the run fails with a
clear error instead of returning invalid data.

## Next steps

- Add libraries, limits and secrets to a stage, or define it in C#: [Stages](stages.md).
- Chain stages: [Pipelines](pipelines.md).
- Test scripts with pytest, without .NET: [Python SDK](python-sdk.md).
- Keep history and versions: [Persistence](persistence.md).
