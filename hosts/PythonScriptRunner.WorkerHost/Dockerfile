# syntax=docker/dockerfile:1
# Imagem do WorkerHost (spec 009): runtime ASP.NET 10 + uv + psrunner do mesmo commit, usuário não-root psr.
# Build a partir da raiz do repositório: docker build -f hosts/PythonScriptRunner.WorkerHost/Dockerfile .
ARG UV_VERSION=0.13.4
# Mesma faixa do global.json (10.0.4xx); a tag 10.0 ainda aponta para 10.0.3xx.
ARG DOTNET_SDK=10.0.402

FROM ghcr.io/astral-sh/uv:${UV_VERSION} AS uv

# psrunner do mesmo commit (D-169): a imagem não depende do PyPI para o SDK.
FROM --platform=$BUILDPLATFORM ghcr.io/astral-sh/uv:${UV_VERSION}-python3.12-trixie-slim AS psrunner
WORKDIR /src
COPY python/psrunner/ .
RUN uv build --wheel --out-dir /wheel

# Compila na arquitetura da máquina e publica para a de destino (multi-arch sem emulação).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK} AS build
ARG TARGETARCH
ARG VERSION=0.0.0-docker
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
COPY schemas/ schemas/
COPY hosts/Directory.Build.props hosts/
COPY hosts/PythonScriptRunner.ServiceDefaults/ hosts/PythonScriptRunner.ServiceDefaults/
COPY hosts/PythonScriptRunner.WorkerHost/ hosts/PythonScriptRunner.WorkerHost/
RUN arch=$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64) \
 && dotnet publish hosts/PythonScriptRunner.WorkerHost -c Release -a "$arch" --self-contained false -o /app \
      -p:MinVerVersionOverride="$VERSION"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.title="psr-worker" \
      org.opencontainers.image.description="PythonScriptRunner worker: runs queued Python stages and cron schedules." \
      org.opencontainers.image.source="https://github.com/ScriptRunnerKit/PythonScriptRunner" \
      org.opencontainers.image.licenses="MIT"
# Pré-aquecimento opcional: --build-arg PSR_PREINSTALL_PYTHON=3.12 instala o intérprete na imagem.
ARG PSR_PREINSTALL_PYTHON=
COPY --from=uv /uv /uvx /usr/local/bin/
RUN groupadd --gid 10001 psr \
 && useradd --uid 10001 --gid psr --home-dir /var/lib/psr --no-create-home --shell /usr/sbin/nologin psr \
 && mkdir -p /var/lib/psr/envs /var/lib/psr/python /tmp/psr /opt/psr \
 && chown -R psr:psr /var/lib/psr /tmp/psr
COPY --from=psrunner /wheel/ /opt/psr/
COPY --chmod=755 hosts/docker/entrypoint.sh /opt/psr/entrypoint.sh
ENV HOME=/var/lib/psr \
    TMPDIR=/tmp/psr/tmp \
    ASPNETCORE_HTTP_PORTS=8080 \
    UV_PYTHON_INSTALL_DIR=/var/lib/psr/python \
    UV_CACHE_DIR=/var/lib/psr/envs/.uv-cache \
    PythonScriptRunner__UvPath=/usr/local/bin/uv \
    PythonScriptRunner__WorkRoot=/tmp/psr \
    PythonScriptRunner__EnvironmentCacheRoot=/var/lib/psr/envs
USER psr
RUN if [ -n "$PSR_PREINSTALL_PYTHON" ]; then uv python install "$PSR_PREINSTALL_PYTHON"; fi
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
VOLUME ["/var/lib/psr/envs", "/var/lib/psr/python", "/tmp/psr"]
ENTRYPOINT ["/opt/psr/entrypoint.sh", "dotnet", "PythonScriptRunner.WorkerHost.dll"]
