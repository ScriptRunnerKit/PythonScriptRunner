# Contributing to PythonScriptRunner

Thanks for helping! The [ScriptRunnerKit contributing guide](https://github.com/ScriptRunnerKit/.github/blob/main/CONTRIBUTING.md)
and the [Code of Conduct](https://github.com/ScriptRunnerKit/.github/blob/main/CODE_OF_CONDUCT.md) apply here. This file
adds what is specific to this repository: the tools, the checks the CI runs and how releases work.

Before anything bigger than a small fix, open an issue to agree on the behavior and its acceptance criteria. Report
security problems privately, as described in [SECURITY.md](SECURITY.md), never in a public issue.

## Prerequisites

- .NET 10 SDK (the version in `global.json`).
- [uv](https://docs.astral.sh/uv/): the integration tests create real Python environments.
- Node.js 24 (`panel/.nvmrc`): the dashboard UI, and Release builds that embed it.
- Docker: the tests for PostgreSQL, RabbitMQ, the worker, the dashboard, the containers and the Aspire AppHost use
  Testcontainers or Aspire.

## Build and test

```bash
dotnet build
dotnet test --solution tests/PythonScriptRunner.SemE2E.slnf     # unit, integration and dashboard tests
dotnet test --project tests/PythonScriptRunner.Containers.Tests  # host images and compose
dotnet test --project tests/PythonScriptRunner.AppHost.Tests     # Aspire AppHost
dotnet build tests/PythonScriptRunner.Panel.E2E.Tests -p:PsrBuildPanel=true
dotnet test --project tests/PythonScriptRunner.Panel.E2E.Tests --no-build   # run it on its own

cd python/psrunner && uv sync && uv run pytest
cd panel && npm ci && npm test
```

To run everything locally with .NET Aspire, see [Hosting](guides/en/hosting.md#local-environment-net-aspire).

### Test projects

| Project | Needs | Notes |
|---------|-------|-------|
| `PythonScriptRunner.Core.Tests` | uv | Unit tests and integration with real Python processes. |
| `PythonScriptRunner.EntityFrameworkCore.Tests`, `.RabbitMQ.Tests`, `.Worker.Tests`, `.Dashboard.Tests` | Docker | PostgreSQL 16, RabbitMQ 3.13 and Mailpit with Testcontainers. |
| `PythonScriptRunner.Containers.Tests` | Docker | Builds both host images and runs them as in production (read-only, tmpfs volumes). |
| `PythonScriptRunner.AppHost.Tests` | Docker, Node.js and uv | Starts the Aspire AppHost and runs the sample pipelines. |
| `PythonScriptRunner.Panel.E2E.Tests` | Docker and the embedded UI | Drives Chromium with Playwright against a real dashboard, worker, PostgreSQL and RabbitMQ. |

Details of each project:
- **Containers.Tests:**
  - The compose test needs the Aspire CLI (`PSR_ASPIRE_CLI` points to it if it is not on the PATH).
  - To test images you built yourself, set `PSR_TEST_WORKER_IMAGE` and `PSR_TEST_DASHBOARD_IMAGE`.
  - When a test fails, the logs of its containers are saved to `TestResults/container-logs/<test>/` next to the test
    assembly, and the end of the worker and dashboard logs goes to the test output.
- **Panel.E2E.Tests:**
  - Build it with `-p:PsrBuildPanel=true` to embed the UI.
  - Run it on its own: containers starting or stopping in other test projects change the network interfaces, and Chromium
    then aborts requests with `ERR_NETWORK_CHANGED`.
  - `PSR_E2E_HEADED=1` shows the browser.
  - A failed test saves its Playwright trace to `TestResults/traces` (`npx playwright show-trace <file>.zip`).
- **Dashboard performance test:** it uses about 200,000 runs; `PSR_PERF_RUNS_PER_DAY=50000` runs the full average
  volume (90 days).
- **Dashboard OpenAPI document:** it is compared with
  `tests/PythonScriptRunner.Dashboard.Tests/Snapshots/openapi-v1.verified.json`; after you review an intended change,
  `PSR_ACCEPT_SNAPSHOTS=1` updates the snapshot.

### The dashboard UI

The UI lives in `panel/` (Node.js 24). Debug builds of `PythonScriptRunner.Dashboard` embed a placeholder page; in Release
(or with `-p:PsrBuildPanel=true`) MSBuild runs `npm ci` and `npm run build` and embeds `panel/dist`.

```bash
cd panel
npm run dev        # Vite at http://localhost:5173/; /api and /hubs go to the DashboardHost at PSR_DASHBOARD_URL (default http://localhost:5080)
npm test           # Vitest
npm run lint       # ESLint and Prettier
npm run build      # type check, build and the budget of the initial bundle (500 KB gzip)
npm run api        # regenerates src/api/schema.d.ts from the OpenAPI snapshot
```

### Documentation

The guides live in `guides/en` and `guides/pt-br`, with the same pages in both languages. A change in behavior updates
both, and the README when the overview changes.

## What the CI checks

A pull request is merged only when every job of `.github/workflows/ci.yml` is green. Run the same checks locally:

| Area | Checks |
|------|--------|
| .NET | builds with warnings as errors, the latest analyzers and code style enforced in the build; `dotnet format --verify-no-changes`; tests on Linux, and the Core tests on Windows |
| Packages | `dotnet pack` produces the 7 packages with one version and their symbol packages |
| Containers | both Dockerfiles build; the images run read-only as a non-root user; the memory limit and the compose from `aspire publish` work |
| Python SDK | `uv run ruff check src tests`, `uv run ruff format --check src tests`, `uv run mypy` (strict) and `uv run pytest`, on Python 3.10 to 3.13 |
| Panel | `npm run lint`, `npm run typecheck`, `npm run api:check`, `npm test` and `npm run build` (initial bundle ≤ 500 KB gzip) |
| Dependencies | no known vulnerable NuGet packages, no high or critical npm advisories in runtime packages, `pip-audit` clean |

## Conventions

- **Tests:** every acceptance criterion and every fixed bug has a test. Integration tests run against real Python (uv),
  PostgreSQL and RabbitMQ (Testcontainers). A test that asserts a duration gets `[Trait("Category", "Timing")]`: the CI
  runs those alone, one at a time (`--filter-trait "Category=Timing" --parallel none`).
- **Public API:** public types and members have XML documentation (the build generates it and fails on warnings).
- **Packages:** versions live in `Directory.Packages.props` (Central Package Management). Dependabot proposes updates
  every Monday.
- **Database:** a change to the model needs a migration. The design-time factory opens no connection:

  ```bash
  dotnet ef migrations add <Name> --project src/PythonScriptRunner.EntityFrameworkCore
  ```

- **Dashboard API:** the OpenAPI document is a snapshot. After an intended change, accept it with
  `PSR_ACCEPT_SNAPSHOTS=1` and regenerate the UI types with `npm run api` in `panel/`.
- **Contracts:** the JSON Schemas in `schemas/` and the protocol shared with `psrunner` are public contracts. Changing
  them in an incompatible way needs a new schema or protocol version.
- **Changelog:** user-visible changes get a line in [CHANGELOG.md](CHANGELOG.md), under the version being prepared.
- **Commits:** [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`, `test:`,
  `build:`, `ci:`, `chore:`). Commit messages and pull requests describe what changed and why, so that they read on their
  own; link the issue when there is one.

## Releases

Maintainers release from tags on `main`; each tag runs the full CI before anything is published.

- **.NET packages and images:**
  1. Date the version's section in `CHANGELOG.md`.
  2. Push the tag `vX.Y.Z`.
  3. `release.yml` waits for approval in the `release` environment. It then publishes the 7 NuGet packages, the two
     multi-arch images to GHCR and the GitHub Release, with the CHANGELOG section as its notes.

  MinVer takes the version from the tag. A tag with a suffix (`v0.2.0-rc.1`) is published as a prerelease.
- **psrunner:**
  1. Set the version in `python/psrunner/src/psrunner/_version.py`.
  2. Push the tag `python/vX.Y.Z`.
  3. `release-python.yml` publishes it to PyPI with trusted publishing.
