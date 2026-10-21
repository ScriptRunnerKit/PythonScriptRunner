# Hosting

English | [Português](../pt-br/hosting.md)

You do not need to write a host application. The repository ships two ready-made hosts as container images, a local
environment on .NET Aspire, and a Docker Compose file generated from it.

## Container images

The images are multi-arch (amd64 and arm64). Each release publishes the version tag (`0.1.0`), the minor tag (`0.1`) and
`latest`.

| Image | Host |
|-------|------|
| `ghcr.io/scriptrunnerkit/psr-worker` | `WorkerHost`: runs queued steps and schedules |
| `ghcr.io/scriptrunnerkit/psr-dashboard` | `DashboardHost`: the dashboard with the Vue UI embedded |

Both images share these properties:
- **Base:** `mcr.microsoft.com/dotnet/aspnet:10.0` with `uv`.
- **User:** they run as the non-root user `psr` (uid 10001).
- **Network:** they listen on port 8080, with `/health/live` and `/health/ready`.
- **SDK:** they include the `psrunner` wheel of the same commit, so environments do not download the SDK. Stage
  dependencies still come from PyPI or from your package index.
- **Supply chain:** they carry an SBOM and provenance attestations.
- **Observability:** logs are JSON on stdout, and OpenTelemetry is exported when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.

```bash
docker run -d --read-only \
  --tmpfs /tmp/psr:uid=10001,gid=10001,exec \
  -v psr-envs:/var/lib/psr/envs -v psr-python:/var/lib/psr/python \
  -e ConnectionStrings__psr="Host=pg;Database=psr;Username=psr_app;Password=..." \
  -e ConnectionStrings__rabbitmq="amqp://psr:...@rabbit:5672" \
  ghcr.io/scriptrunnerkit/psr-worker:0.1.0
```

### Environment variables

| Variable | Host | Example or meaning |
|----------|------|--------------------|
| `ConnectionStrings__psr` | both | `Host=pg;Database=psr;Username=psr_app;Password=…` |
| `ConnectionStrings__rabbitmq` | both | `amqps://psr:…@rabbit:5671/psr` |
| `PSR_AUTO_MIGRATE` | both | `true` applies the migrations at startup (default `false`); concurrent hosts wait on an advisory lock |
| `PythonScriptRunner__Worker__MaxConcurrency` | worker | `8` |
| `PythonScriptRunner__Worker__ShutdownGracePeriod` | worker | `00:05:00` |
| `PythonScriptRunner__Secrets__<NAME>` | worker | Secret values, from your platform's secret store |
| `PythonScriptRunner__ThreadsPerProcess` | both | Threads of the numeric libraries (see [Memory limits](#memory-limits)) |
| `PythonScriptRunner__PackageIndexUrl` | both | A private package index |
| `PythonScriptRunner__Dashboard__Login__Users__N__Name`, `__Role`, `__PasswordHash` | dashboard | Fixed login users |
| `Oidc__Authority`, `Oidc__ClientId`, `Oidc__ClientSecret` | dashboard | Turns on OpenID Connect instead of fixed login |
| `Dashboard__RoleClaim` | dashboard | Claim with the roles (default `roles`) |
| `Dashboard__Roles__Reader`, `__Executor`, `__Author`, `__Admin` | dashboard | Role values mapped to each dashboard role |
| `Dashboard__PathPrefix` | dashboard | Default `/psr` |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | dashboard | `true` behind a reverse proxy |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | both | `http://otel-collector:4317` |
| `Serilog__MinimumLevel__Override__<category>` | both | For example, `Information` for `Microsoft.AspNetCore` brings back request logs (default `Warning`) |

Every option of the [configuration reference](configuration.md) can be set the same way, with `__` between sections
(`PythonScriptRunner__DefaultTimeout=00:10:00`).

### Volumes and read-only file system

The images run with a read-only root file system. Python writes only to these volumes:

| Path | Contents | If it is not persistent |
|------|----------|-------------------------|
| `/var/lib/psr/envs` | Cached virtual environments (one per set of dependencies) and the uv cache | Every start recreates the environments, so the first run of each stage is slower |
| `/var/lib/psr/python` | Python interpreters installed by uv | The interpreter is downloaded again |
| `/tmp/psr` | Work folders of the runs and `TMPDIR` | Expected: it only holds temporary files |

A tmpfs works for any of them, but it must allow execution (`exec`), because the volumes run Python; Docker mounts tmpfs
`noexec` by default.

Without access to PyPI, environments fail with `EnvironmentSetupFailed`. In that case, point
`PythonScriptRunner__PackageIndexUrl` to a private index, or build the image with an interpreter already installed:

```bash
docker build --build-arg PSR_PREINSTALL_PYTHON=3.12 -f hosts/PythonScriptRunner.WorkerHost/Dockerfile -t my-psr-worker .
```

### Memory limits

Every Python process gets the memory limit of its stage (1024 MB by default). How it is enforced depends on the platform,
and the host logs the mode it uses at startup:

- **Windows:** a Job Object per process.
- **Linux with a delegable cgroup v2:**
  - each process runs in its own cgroup (`memory.max`, no swap);
  - when the process exceeds the limit, the kernel kills it and the attempt ends with `MemoryLimitExceeded` (code
    `OUT_OF_MEMORY`);
  - the host needs write access to its own cgroup: a systemd service with `Delegate=yes`, or a container running as root
    with a writable cgroup file system.
- **Other Linux setups, including standard containers** (they mount the cgroup file system read-only):
  - an address-space limit (`RLIMIT_AS`);
  - numeric libraries (OpenBLAS, MKL, OpenMP, NumExpr, vecLib) reserve address space per thread, so in this mode they get
    1 thread per process.

`PythonScriptRunner:ThreadsPerProcess` sets the thread count in every mode. Raise it for parallel numeric work, with a
memory limit that has room for it.

## Kubernetes

There is no official Helm chart yet. This worker `Deployment` shows the settings that matter:

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
git clone https://github.com/ScriptRunnerKit/PythonScriptRunner
cd PythonScriptRunner
dotnet run --project hosts/PythonScriptRunner.AppHost
```

The AppHost starts:
- PostgreSQL 16 and RabbitMQ 3.13 (with the management UI);
- Mailpit, a test SMTP server;
- the WorkerHost and the DashboardHost;
- the Vue UI in development mode, with hot reload.

The Aspire dashboard (`http://localhost:15000`) shows the resources, logs and traces of every run. The **Painel** link on
the `dashboard` resource opens the UI.

![Trace of a run in the Aspire dashboard](../images/aspire-trace.png)

The dashboard has one development user per role, all with the password `psr-dev`:

| User | Role |
|------|------|
| `leitor` | Reader |
| `executor` | Executor |
| `autor` | Author |
| `admin` | Admin |

To try the sample pipelines in `tests/fixtures`:
1. Zip the contents of a folder, with `stages/` and `pipelines/` at the root.
2. Import it in the UI as `autor`.
3. Publish the stages, then the pipeline.

## Docker Compose

`aspire publish` turns the AppHost into a `docker-compose.yaml` that uses the published images:

```bash
dotnet tool install --global Aspire.Cli --version 13.6.2
aspire publish --apphost hosts/PythonScriptRunner.AppHost/PythonScriptRunner.AppHost.csproj -o deploy
```

Fill in `deploy/.env`, then start it with `docker compose up -d` in `deploy`:

| Variable | Meaning |
|----------|---------|
| `POSTGRES_PASSWORD` | Password of the `postgres` user |
| `RABBITMQ_PASSWORD` | Password of the RabbitMQ `guest` user |
| `PSR_VERSION` | Image version (default `latest`) |
| `PSR_DASHBOARD_PORT` | Port of the dashboard on the host (default `8080`); the UI is at `/psr/` |

The compose includes an Aspire dashboard for the traces. It is a starting point, and it keeps the development setup:
- the four users with the password `psr-dev`;
- Mailpit;
- the test SMTP secrets.

Before you expose it, replace the `PasswordHash` values with hashes from `psr hash-password` (or configure OpenID
Connect), and remove what you do not use.
