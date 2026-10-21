# syntax=docker/dockerfile:1
# Imagem do DashboardHost (spec 009): API, painel Vue embutido e relay do outbox; inclui o uv porque publicar uma
# Etapa resolve as dependências. Runtime ASP.NET 10, usuário não-root psr.
# Build a partir da raiz do repositório: docker build -f hosts/PythonScriptRunner.DashboardHost/Dockerfile .
ARG UV_VERSION=0.13.4
# Mesma faixa do global.json (10.0.4xx); a tag 10.0 ainda aponta para 10.0.3xx.
ARG DOTNET_SDK=10.0.402

FROM ghcr.io/astral-sh/uv:${UV_VERSION} AS uv

# psrunner do mesmo commit (D-169): a imagem não depende do PyPI para o SDK.
FROM --platform=$BUILDPLATFORM ghcr.io/astral-sh/uv:${UV_VERSION}-python3.12-trixie-slim AS psrunner
WORKDIR /src
COPY python/psrunner/ .
RUN uv build --wheel --out-dir /wheel

# Painel Vue (spec 008; D-155, D-158).
FROM --platform=$BUILDPLATFORM node:24-bookworm-slim AS panel
WORKDIR /panel
COPY panel/package.json panel/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY panel/ .
RUN npm run build

# Compila na arquitetura da máquina e publica para a de destino (multi-arch sem emulação); o painel já vem pronto.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK} AS build
ARG TARGETARCH
ARG VERSION=0.0.0-docker
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
COPY schemas/ schemas/
COPY --from=panel /panel/dist/ panel/dist/
COPY hosts/Directory.Build.props hosts/
COPY hosts/PythonScriptRunner.ServiceDefaults/ hosts/PythonScriptRunner.ServiceDefaults/
COPY hosts/PythonScriptRunner.DashboardHost/ hosts/PythonScriptRunner.DashboardHost/
RUN arch=$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64) \
 && dotnet publish hosts/PythonScriptRunner.DashboardHost -c Release -a "$arch" --self-contained false -o /app \
      -p:MinVerVersionOverride="$VERSION" -p:PsrPanelPrebuilt=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0
COPY --from=uv /uv /uvx /usr/local/bin/
RUN groupadd --gid 10001 psr \
 && useradd --uid 10001 --gid psr --home-dir /var/lib/psr --no-create-home --shell /usr/sbin/nologin psr \
 && mkdir -p /var/lib/psr/envs /var/lib/psr/python /tmp/psr /opt/psr \
 && chown -R psr:psr /var/lib/psr /tmp/psr
COPY --from=psrunner /wheel/ /opt/psr/
COPY --chmod=755 hosts/docker/entrypoint.sh /opt/psr/entrypoint.sh
ENV HOME=/var/lib/psr \
    ASPNETCORE_HTTP_PORTS=8080 \
    UV_PYTHON_INSTALL_DIR=/var/lib/psr/python \
    UV_CACHE_DIR=/var/lib/psr/envs/.uv-cache \
    PythonScriptRunner__UvPath=/usr/local/bin/uv \
    PythonScriptRunner__WorkRoot=/tmp/psr \
    PythonScriptRunner__EnvironmentCacheRoot=/var/lib/psr/envs
USER psr
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
VOLUME ["/var/lib/psr/envs", "/var/lib/psr/python", "/tmp/psr"]
ENTRYPOINT ["/opt/psr/entrypoint.sh", "dotnet", "PythonScriptRunner.DashboardHost.dll"]
