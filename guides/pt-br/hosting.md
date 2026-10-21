# Hospedagem

[English](../en/hosting.md) | Português

Você não precisa escrever uma aplicação host. O repositório traz dois hosts prontos como imagens de contêiner, um ambiente
local no .NET Aspire e um Docker Compose gerado a partir dele.

## Imagens de contêiner

As imagens são multi-arch (amd64 e arm64). Cada release publica a tag da versão (`0.1.0`), a tag da versão menor (`0.1`)
e `latest`.

| Imagem | Host |
|--------|------|
| `ghcr.io/scriptrunnerkit/psr-worker` | `WorkerHost`: executa os passos enfileirados e os agendamentos |
| `ghcr.io/scriptrunnerkit/psr-dashboard` | `DashboardHost`: o painel, com a interface Vue embutida |

As duas imagens têm estas características:
- **Base:** `mcr.microsoft.com/dotnet/aspnet:10.0` com o `uv`.
- **Usuário:** rodam com o usuário não-root `psr` (uid 10001).
- **Rede:** escutam na porta 8080, com `/health/live` e `/health/ready`.
- **SDK:** trazem o wheel do `psrunner` do mesmo commit, então os ambientes não baixam o SDK. As dependências das Etapas
  continuam vindo do PyPI ou do seu índice de pacotes.
- **Cadeia de suprimentos:** trazem atestados de SBOM e proveniência.
- **Observabilidade:** os logs saem em JSON no stdout, e o OpenTelemetry é exportado quando `OTEL_EXPORTER_OTLP_ENDPOINT`
  está definido.

```bash
docker run -d --read-only \
  --tmpfs /tmp/psr:uid=10001,gid=10001,exec \
  -v psr-envs:/var/lib/psr/envs -v psr-python:/var/lib/psr/python \
  -e ConnectionStrings__psr="Host=pg;Database=psr;Username=psr_app;Password=..." \
  -e ConnectionStrings__rabbitmq="amqp://psr:...@rabbit:5672" \
  ghcr.io/scriptrunnerkit/psr-worker:0.1.0
```

### Variáveis de ambiente

| Variável | Host | Exemplo ou significado |
|----------|------|------------------------|
| `ConnectionStrings__psr` | ambos | `Host=pg;Database=psr;Username=psr_app;Password=…` |
| `ConnectionStrings__rabbitmq` | ambos | `amqps://psr:…@rabbit:5671/psr` |
| `PSR_AUTO_MIGRATE` | ambos | `true` aplica as migrações na inicialização (padrão `false`); hosts simultâneos esperam num advisory lock |
| `PythonScriptRunner__Worker__MaxConcurrency` | worker | `8` |
| `PythonScriptRunner__Worker__ShutdownGracePeriod` | worker | `00:05:00` |
| `PythonScriptRunner__Secrets__<NOME>` | worker | Valores dos segredos, vindos do cofre de segredos da sua plataforma |
| `PythonScriptRunner__ThreadsPerProcess` | ambos | Threads das bibliotecas numéricas (veja [Limites de memória](#limites-de-memória)) |
| `PythonScriptRunner__PackageIndexUrl` | ambos | Um índice de pacotes privado |
| `PythonScriptRunner__Dashboard__Login__Users__N__Name`, `__Role`, `__PasswordHash` | painel | Usuários do login fixo |
| `Oidc__Authority`, `Oidc__ClientId`, `Oidc__ClientSecret` | painel | Liga o OpenID Connect no lugar do login fixo |
| `Dashboard__RoleClaim` | painel | Claim com os papéis (padrão `roles`) |
| `Dashboard__Roles__Reader`, `__Executor`, `__Author`, `__Admin` | painel | Valores associados a cada papel do painel |
| `Dashboard__PathPrefix` | painel | Padrão `/psr` |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | painel | `true` atrás de um proxy reverso |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | ambos | `http://otel-collector:4317` |
| `Serilog__MinimumLevel__Override__<categoria>` | ambos | Por exemplo, `Information` para `Microsoft.AspNetCore` traz de volta os logs de requisição (padrão `Warning`) |

Qualquer opção da [referência de configuração](configuration.md) pode ser definida do mesmo jeito, com `__` entre as
seções (`PythonScriptRunner__DefaultTimeout=00:10:00`).

### Volumes e sistema de arquivos somente leitura

As imagens rodam com o sistema de arquivos raiz somente leitura. O Python grava apenas nestes volumes:

| Caminho | Conteúdo | Se não for persistente |
|---------|----------|------------------------|
| `/var/lib/psr/envs` | Ambientes virtuais em cache (um por conjunto de dependências) e o cache do uv | Cada inicialização recria os ambientes, então a primeira execução de cada Etapa fica mais lenta |
| `/var/lib/psr/python` | Interpretadores Python instalados pelo uv | O interpretador é baixado de novo |
| `/tmp/psr` | Pastas de trabalho das execuções e o `TMPDIR` | Esperado: só guarda arquivos temporários |

Um tmpfs funciona em qualquer um deles, mas precisa permitir execução (`exec`), porque os volumes executam Python; o
Docker monta tmpfs com `noexec` por padrão.

Sem acesso ao PyPI, os ambientes falham com `EnvironmentSetupFailed`. Nesse caso, aponte
`PythonScriptRunner__PackageIndexUrl` para um índice privado, ou construa a imagem com um interpretador já instalado:

```bash
docker build --build-arg PSR_PREINSTALL_PYTHON=3.12 -f hosts/PythonScriptRunner.WorkerHost/Dockerfile -t meu-psr-worker .
```

### Limites de memória

Todo processo Python recebe o limite de memória da sua Etapa (1024 MB por padrão). A forma de aplicar depende da
plataforma, e o host registra no log o modo que está usando ao iniciar:

- **Windows:** um Job Object por processo.
- **Linux com cgroup v2 delegável:**
  - cada processo roda no próprio cgroup (`memory.max`, sem swap);
  - quando o processo passa do limite, o kernel o encerra e a tentativa termina com `MemoryLimitExceeded` (código
    `OUT_OF_MEMORY`);
  - o host precisa de permissão de escrita no próprio cgroup: um serviço systemd com `Delegate=yes`, ou um contêiner
    rodando como root com o sistema de arquivos de cgroup gravável.
- **Outros cenários Linux, inclusive contêineres padrão** (eles montam o sistema de arquivos de cgroup somente leitura):
  - um limite de espaço de endereçamento (`RLIMIT_AS`);
  - as bibliotecas numéricas (OpenBLAS, MKL, OpenMP, NumExpr, vecLib) reservam espaço de endereçamento por thread, então
    nesse modo recebem 1 thread por processo.

O `PythonScriptRunner:ThreadsPerProcess` define o número de threads em todos os modos. Aumente para trabalho numérico em
paralelo, com um limite de memória que comporte isso.

## Kubernetes

Ainda não há um Helm chart oficial. Este `Deployment` de worker mostra as configurações que importam:

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
      # Maior que PythonScriptRunner__Worker__ShutdownGracePeriod (padrão 5 min), para os passos em andamento terminarem.
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
            limits: { memory: 4Gi }   # espaço para MaxConcurrency processos no limite da Etapa
          volumeMounts:
            - { name: envs, mountPath: /var/lib/psr/envs }
            - { name: python, mountPath: /var/lib/psr/python }
            - { name: work, mountPath: /tmp/psr }
      volumes:
        - { name: envs, emptyDir: {} }     # um PersistentVolumeClaim mantém os ambientes entre reinícios
        - { name: python, emptyDir: {} }
        - { name: work, emptyDir: {} }
```

Se o `terminationGracePeriodSeconds` for menor que o `ShutdownGracePeriod`, o kubelet encerra tentativas que ainda estão
rodando. Elas seguem então as regras do lease: são retomadas em outro worker ou falham com `WorkerLost`.

## Ambiente local (.NET Aspire)

Com Docker, o SDK do .NET 10, Node.js 24 e o uv instalados:

```bash
git clone https://github.com/ScriptRunnerKit/PythonScriptRunner
cd PythonScriptRunner
dotnet run --project hosts/PythonScriptRunner.AppHost
```

O AppHost sobe:
- PostgreSQL 16 e RabbitMQ 3.13 (com a interface de gerenciamento);
- Mailpit, um servidor SMTP de teste;
- o WorkerHost e o DashboardHost;
- a interface Vue em modo de desenvolvimento, com hot reload.

O dashboard do Aspire (`http://localhost:15000`) mostra os recursos, os logs e os traces de cada execução. O link
**Painel** no recurso `dashboard` abre a interface.

![Trace de uma execução no dashboard do Aspire](../images/aspire-trace.png)

O painel tem um usuário de desenvolvimento por papel, todos com a senha `psr-dev`:

| Usuário | Papel |
|---------|-------|
| `leitor` | Reader |
| `executor` | Executor |
| `autor` | Author |
| `admin` | Admin |

Para testar as trilhas de exemplo em `tests/fixtures`:
1. Compacte o conteúdo de uma pasta, com `stages/` e `pipelines/` na raiz do zip.
2. Importe no painel como `autor`.
3. Publique as Etapas e depois a trilha.

## Docker Compose

O `aspire publish` transforma o AppHost num `docker-compose.yaml` que usa as imagens publicadas:

```bash
dotnet tool install --global Aspire.Cli --version 13.6.2
aspire publish --apphost hosts/PythonScriptRunner.AppHost/PythonScriptRunner.AppHost.csproj -o deploy
```

Preencha o `deploy/.env` e suba com `docker compose up -d` dentro de `deploy`:

| Variável | Significado |
|----------|-------------|
| `POSTGRES_PASSWORD` | Senha do usuário `postgres` |
| `RABBITMQ_PASSWORD` | Senha do usuário `guest` do RabbitMQ |
| `PSR_VERSION` | Versão das imagens (padrão `latest`) |
| `PSR_DASHBOARD_PORT` | Porta do painel no host (padrão `8080`); a interface fica em `/psr/` |

O compose inclui um dashboard do Aspire para os traces. Ele é um ponto de partida e mantém a configuração de
desenvolvimento:
- os quatro usuários com a senha `psr-dev`;
- o Mailpit;
- os segredos SMTP de teste.

Antes de expor, troque os valores de `PasswordHash` por hashes gerados com `psr hash-password` (ou configure o OpenID
Connect) e remova o que não for usar.
