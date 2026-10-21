# Changelog

All notable changes to PythonScriptRunner are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The NuGet packages and the container images share one
version; the `psrunner` SDK has its own version on PyPI.

## [Unreleased]

### Added

- Guides in English and Portuguese (`guides/`): getting started and the ways to use the library, stages, pipelines,
  the Python SDK, persistence, queue and scheduling, the dashboard, hosting, a configuration reference and how it
  works.

### Changed

- The README covers every capability with short examples and links to the guides. The `psrunner` README, the PyPI
  page, is now a complete reference of the SDK.
- `psrunner` 0.1.1 publishes that README on PyPI. The code is the same as 0.1.0.

### Fixed

- Dashboard: in the field builder of the stage editor, the label of the "required" switch no longer breaks into
  syllables in the narrow side panel.

## [0.1.0] - 2026-10-21

The first release.

### Added

- **Core runner** (`PythonScriptRunner.Core`, `PythonScriptRunner.Abstractions`):
  - stages with typed inputs and outputs (12 field types, including `table`, `file` and `list<T>`), defined in
    `stage.json` or in C#;
  - each attempt runs in an isolated process: a clean environment, its own work folder, a timeout and a memory limit
    (Job Object on Windows; cgroup v2 when it can be delegated on Linux, otherwise `RLIMIT_AS` with
    `ThreadsPerProcess`);
  - secrets reach only the stages that declare them, and are masked in the captured output;
  - Python environments created and cached by uv, one per set of dependencies.
- **Pipelines:**
  - steps bound to the pipeline's inputs, to earlier steps' outputs or to constants;
  - types and bindings validated when the catalog loads;
  - defined in `*.pipeline.json` or in C#.
- **`psrunner` 0.1.0**, the Python SDK:
  - `@stage`, `Context`, `File` and `StageError`;
  - `psrunner.testing.run_stage`, to test stages with pytest without .NET.
- **Persistence** (`PythonScriptRunner.EntityFrameworkCore`, PostgreSQL 16+):
  - a versioned catalog (draft, published, archived), with dependencies locked by hash on publish and zip import and
    export;
  - the full history of runs, attempts, logs and files;
  - idempotency keys, an audit log and partitioned retention.
- **Queue** (`PythonScriptRunner.RabbitMQ`, RabbitMQ 3.13+):
  - queued runs through a transactional outbox;
  - the public `StartPipelineRun` command, `run.*` and `step.*` events, and subscriptions for event handlers.
- **Worker** (`PythonScriptRunner.Worker`):
  - leases, retries with exponential backoff, recovery of lost workers and cancellation;
  - concurrency limits per worker and per pipeline;
  - cron schedules with IANA time zones and catch-up runs.
- **Dashboard** (`PythonScriptRunner.Dashboard`):
  - an embeddable dashboard with an internal API and real-time updates (SignalR);
  - a Vue UI to edit, test and publish stages and pipelines, follow runs live, and manage schedules, the audit log and
    administration;
  - four cumulative roles, and access by fixed login, a custom filter, host authentication or local requests.
- **`psr` tool** (`PythonScriptRunner.Tool`): `psr hash-password` generates the password hashes for fixed login.
- **Hosts and images:**
  - `WorkerHost` and `DashboardHost`, configured by environment variables, with health checks, JSON logs, OpenTelemetry
    and optional migrations at startup (`PSR_AUTO_MIGRATE`). The DashboardHost also supports OpenID Connect;
  - multi-arch images `ghcr.io/scriptrunnerkit/psr-worker` and `ghcr.io/scriptrunnerkit/psr-dashboard`: non-root,
    read-only root file system, with an SBOM and provenance.
- **Local environment:** a .NET Aspire AppHost with PostgreSQL, RabbitMQ, Mailpit, both hosts and the UI in development
  mode. `aspire publish` generates a Docker Compose file from it.

### Compatibility

| PythonScriptRunner (NuGet and images) | .NET | Protocol | `psrunner` | Python |
|---------------------------------------|------|----------|------------|--------|
| 0.1.0 | 10 | 1 | 0.1.x | 3.10–3.13 |

[Unreleased]: https://github.com/ScriptRunnerKit/PythonScriptRunner/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/ScriptRunnerKit/PythonScriptRunner/releases/tag/v0.1.0
