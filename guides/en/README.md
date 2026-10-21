# PythonScriptRunner guides

English | [Português](../pt-br/README.md)

PythonScriptRunner runs Python scripts from .NET with a typed contract. These guides go from the smallest setup (the
library alone, with no database and no message broker) to the complete one, with a queue, scheduling and an embedded
dashboard.

| Guide | What it covers |
|-------|----------------|
| [Getting started](getting-started.md) | The ways to use it, what to install, and your first stage in a few minutes |
| [Stages](stages.md) | The contract (inputs, outputs, the 12 field types), `stage.json`, stages in C#, limits, retry and secrets |
| [Pipelines](pipelines.md) | Chaining stages, bindings, pipeline outputs and failure handling |
| [Python SDK](python-sdk.md) | Writing scripts with `psrunner`, files, errors, and testing with pytest and the CLI |
| [Persistence (PostgreSQL)](persistence.md) | Versioned catalog, run history, retention and audit |
| [Queue, worker and scheduling (RabbitMQ)](queue-and-worker.md) | Background runs, retry, cron, events and integration with other systems |
| [Dashboard](dashboard.md) | The embedded web UI, access modes and roles |
| [Hosting](hosting.md) | Container images, volumes, memory limits, Kubernetes, .NET Aspire and Docker Compose |
| [Configuration reference](configuration.md) | Every option with its default value |
| [How it works](how-it-works.md) | Process isolation, Python environments, limits, security model and observability |

The [README](../../README.md) has an overview of everything on one page.
