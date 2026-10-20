# PythonScriptRunner

Biblioteca .NET 10 para executar Python de forma tipada, encadeável e observável: etapas (**Stages**)
com contrato de entrada/saída, trilhas (**Pipelines**), execução assíncrona com RabbitMQ, agendamento
cron e painel Vue opcional.

> Projeto em desenvolvimento. Esta versão entrega o **Core Runner** (execução síncrona de Stages e de
> trilhas), o SDK Python **`psrunner`**, a **persistência em PostgreSQL** (catálogo versionado e histórico), a
> **execução assíncrona** com RabbitMQ, worker e agendamento cron e o **painel embutível** (API, tempo real e acesso
> configurável; a interface Vue chega numa próxima versão).

## Como funciona

Cada Stage declara suas entradas e saídas (12 tipos: `number`, `integer`, `decimal`, `string`, `bool`,
`date`, `datetime`, `enum`, `json`, `table`, `file`, `list<T>`) e tem exatamente um script Python.
O runner valida as entradas, executa o script num processo isolado (ambiente limpo, timeout, limite
de memória, segredos injetados só nele), valida as saídas e devolve o resultado tipado.

```python
# stages/progressao-termos/script.py
from psrunner import stage

@stage
def run(inputs, ctx):
    a1, razao, n = inputs["a1"], inputs["razao"], inputs["n"]
    termos = [a1 * razao**i for i in range(n)]
    ctx.log.info("gerados %d termos", n)
    return {"termos": termos}
```

```jsonc
// stages/progressao-termos/stage.json
{
  "$schema": "https://raw.githubusercontent.com/net0well/PythonScriptRunner/main/schemas/stage.v1.json",
  "key": "progressao-termos",
  "version": 1,
  "name": "Termos de progressão",
  "inputs": [
    { "name": "a1", "type": "number" },
    { "name": "razao", "type": "number" },
    { "name": "n", "type": "integer" }
  ],
  "outputs": [ { "name": "termos", "type": "list", "itemType": "number" } ]
}
```

```csharp
builder.Services
    .AddPythonScriptRunner()
    .AddFileCatalog("./psr");                       // pasta com stages/<chave>/stage.json

// ...
await using var result = await runner.RunStageAsync("progressao-termos", new { a1 = 1, razao = 2, n = 4 });
result.EnsureSuccess();
double[] termos = result.GetOutput<double[]>("termos");   // [1, 2, 4, 8]
```

Stages também podem ser definidos em C# (`AddStage("chave", s => s.Script(...).Input(...).Output(...))`)
e testados localmente com pytest pelo `psrunner.testing.run_stage`, sem .NET.

## Trilhas (pipelines)

Uma trilha encadeia Stages em ordem. Cada entrada de um passo recebe a entrada da trilha
(`$pipeline.campo`), a saída de qualquer passo anterior (`$steps.passo.campo`) ou um valor fixo
(`{"const": ...}`). Sem ligação explícita, vale o campo de mesmo nome do passo anterior. A trilha
declara as próprias saídas. Tipos e ligações são validados ao carregar o catálogo.

```jsonc
// pipelines/calculo-progressao.pipeline.json
{
  "key": "calculo-progressao", "version": 1, "name": "Cálculo de progressão",
  "inputs": [ { "name": "a1", "type": "number" }, { "name": "razao", "type": "number" },
              { "name": "n", "type": "integer" }, { "name": "coeficientes", "type": "list", "itemType": "number" } ],
  "steps": [
    { "key": "termos", "stage": "progressao-termos@1" },
    { "key": "polinomio", "stage": "polinomio-avaliar@1",
      "bindings": { "coeficientes": "$pipeline.coeficientes", "x": "$steps.termos.termos" } },
    { "key": "estatisticas", "stage": "estatisticas@1", "bindings": { "valores": "$steps.polinomio.valores" } }
  ],
  "outputs": [ { "name": "soma", "source": "$steps.estatisticas.soma" } ]
}
```

```csharp
await using var result = await runner.RunPipelineAsync("calculo-progressao",
    new { a1 = 1, razao = 2, n = 4, coeficientes = new[] { 1.0, 0.0, 1.0 } });
double soma = result.GetOutput<double>("soma");
double[] termos = result.GetStepOutput<double[]>("termos", "termos");   // depuração
```

A mesma trilha pode ser definida em C# com `AddPipeline("chave", p => p.Input(...).Step(...).Output(...))`.
Se um passo falhar, os seguintes ficam como `Skipped` e a execução termina `Failed`.

## Persistência (PostgreSQL)

Com o pacote `PythonScriptRunner.EntityFrameworkCore`, Stages e trilhas passam a viver no banco (schema `psr`) com
versões imutáveis (rascunho → publicado → arquivado), e **toda** execução fica registrada: passos, tentativas, cauda
de stdout/stderr e arquivos (em `bytea`, lidos e gravados em streaming).

```csharp
builder.Services
    .AddPythonScriptRunner()
    .AddEntityFrameworkCore(o =>
    {
        o.UseNpgsql(builder.Configuration.GetConnectionString("psr")!);   // PostgreSQL 16+
        o.Retention.Artifacts = TimeSpan.FromDays(7);                     // arquivos
        o.Retention.Runs = TimeSpan.FromDays(90);                         // histórico
    });
builder.Services.AddHealthChecks().AddPythonScriptRunnerDatabase();

var app = builder.Build();
await app.Services.MigratePythonScriptRunnerAsync();   // explícito; ou GeneratePythonScriptRunnerMigrationScriptAsync()
```

- **Catálogo:** `ICatalogAdmin` cria rascunhos (concorrência otimista), publica (verifica a sintaxe com o Python
  declarado e trava as dependências com `uv pip compile --generate-hashes`), arquiva, exclui, testa rascunhos e
  importa/exporta zip no formato de pasta.
- **Execuções:** `IRunManager` consulta (paginação keyset), cancela e reprocessa uma execução que falhou — os passos
  que deram certo reaproveitam as saídas e o que falhou ganha uma nova tentativa. `RunOptions.IdempotencyKey` devolve
  a mesma execução por 24 h.
- **Retenção:** o histórico é particionado (semana para execuções, dia para conteúdo de arquivos, mês para auditoria);
  um job de manutenção cria partições à frente e remove as antigas. Uma trilha pode encurtar a retenção.
- **Auditoria:** ações humanas (`Actor.User(...)`) ficam em `psr.audit_events`; disparos automáticos ficam na própria execução.

## Fila, worker e agendamento (RabbitMQ)

Com `PythonScriptRunner.RabbitMQ` e `PythonScriptRunner.Worker`, execuções podem ser enfileiradas. Cada passo é uma
mensagem: o worker assume o passo com um *lease*, executa o processo Python e grava o resultado e a mensagem do próximo
passo na mesma transação (outbox no PostgreSQL). Enfileirar funciona mesmo com o broker fora do ar.

```csharp
builder.Services
    .AddPythonScriptRunner()
    .AddEntityFrameworkCore(o => o.UseNpgsql(builder.Configuration.GetConnectionString("psr")!))
    .AddRabbitMq(o => o.ConnectionString = builder.Configuration.GetConnectionString("rabbitmq"))   // RabbitMQ 3.13+
    .AddWorker(o => o.MaxConcurrency = 8)                         // só nos processos que executam passos
    .AddRunEventHandler<NotificarErp>(s => s.Subscription("erp").On("run.succeeded.calculo-progressao", "run.failed.*"));
builder.Services.AddHealthChecks().AddPythonScriptRunnerChecks();  // banco, broker e uv

// ...
RunHandle run = await queue.EnqueuePipelineAsync("calculo-progressao", new { a1 = 1, razao = 2, n = 4, coeficientes = new[] { 1, 0, 1 } });
```

- **Confiabilidade:** retry automático por Stage com atraso exponencial; worker perdido é detectado pelo *lease* e o
  passo é retomado (Stage idempotente ou com retry restante) ou falha com `WorkerLost` para reprocesso manual;
  mensagens duplicadas não executam o passo duas vezes.
- **Concorrência:** `MaxConcurrency` por worker e `max_concurrent_runs` por trilha (as demais esperam como `Pending`, em ordem).
- **Cancelamento:** `IRunManager.CancelAsync` encerra a árvore de processos no worker que executa o passo.
- **Agendamento:** `ICatalogAdmin.CreateScheduleAsync` com expressão cron e fuso IANA (padrão `America/Sao_Paulo`);
  ocorrências perdidas viram uma execução de recuperação; vários workers nunca disparam a mesma ocorrência.
- **Sistemas externos:** o comando público `StartPipelineRun` (exchange `psr.public`) inicia trilhas e os eventos
  `run.*`/`step.*` (exchange `psr.events`) informam o andamento e as saídas. Contratos em `schemas/messages`.
- **Observabilidade:** `ActivitySource` e `Meter` `PythonScriptRunner`; o `traceparent` acompanha a execução até o worker.

O host `hosts/PythonScriptRunner.WorkerHost` é um worker pronto, configurado por variáveis de ambiente
(`ConnectionStrings__psr`, `ConnectionStrings__rabbitmq`, `PythonScriptRunner__Worker__MaxConcurrency`,
`PythonScriptRunner__Secrets__<NOME>`, `PSR_AUTO_MIGRATE=true`), com `/health/live`, `/health/ready`, logs JSON e OTLP.

## Painel (Dashboard)

Com `PythonScriptRunner.Dashboard`, o painel é embutido na sua aplicação, no estilo do Hangfire: API interna, atualizações
em tempo real (SignalR) e a interface servida no prefixo escolhido. Sem `MapPythonScriptRunnerDashboard` não existe painel.

```csharp
builder.Services
    .AddPythonScriptRunner()
    .AddEntityFrameworkCore(o => o.UseNpgsql(builder.Configuration.GetConnectionString("psr")!))
    .AddRabbitMq(o => o.ConnectionString = builder.Configuration.GetConnectionString("rabbitmq"))
    .AddDashboard(o => o.UseFixedLogin(login => login.BindConfiguration()));   // seção PythonScriptRunner:Dashboard:Login

var app = builder.Build();
app.MapPythonScriptRunnerDashboard("/psr");   // API em /psr/api, tempo real em /psr/hubs/runs, painel em /psr/
```

Escolha **um** modo de acesso; os papéis são cumulativos: `Reader` (vê tudo) → `Executor` (dispara, cancela,
reprocessa) → `Author` (edita, testa, publica, importa, agenda) → `Admin` (exclui, retenção, segredos, auditoria).

| Modo | Configuração |
|------|--------------|
| Login fixo | `o.UseFixedLogin(l => l.BindConfiguration())` com usuários na configuração (abaixo) ou `l.Users` em código |
| Filtro próprio | `o.UseAuthorizationFilter<MeuFiltro>()`: `IDashboardAuthorizationFilter` devolve `DashboardUser(nome, papel)` ou `null` |
| Autenticação do host | `o.UseHostAuthentication(r => { r.Reader = p => p.RequireRole("psr-leitor"); /* Executor, Author, Admin */ r.LoginUrl = "/conta/entrar?returnUrl={returnUrl}"; })` |
| Somente local | `o.AllowLocalRequests = true` — só em Development (fora dele a inicialização falha); soma-se a um modo principal |

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

A senha nunca vai para a configuração, só o hash (PBKDF2, formato do ASP.NET Identity), gerado pela ferramenta `psr`:

```bash
dotnet tool install -g PythonScriptRunner.Tool
psr hash-password                              # digita a senha duas vezes, sem eco
echo "senha" | psr hash-password --stdin       # em scripts
```

No login fixo a sessão é um cookie `psr.session` (HttpOnly, `SameSite=Lax`) de 8 h deslizantes e no máximo 24 h; 5
falhas seguidas bloqueiam o usuário por 15 min e um IP pode tentar 10 vezes por minuto. Alterações na configuração valem
sem reiniciar (usuário removido ou senha trocada perde a sessão; papel novo vale na hora). As chaves do cookie ficam em
`psr.dashboard_keys`, então a sessão vale em todas as instâncias e sobrevive a reinícios.

Na API, requisições que alteram algo exigem o cabeçalho `X-PSR-Request: 1`, edições usam `ETag`/`If-Match` e erros
seguem `ProblemDetails` com `code`. Em Development, o documento OpenAPI (interno) fica em `/psr/api/openapi/v1.json`.

O host `hosts/PythonScriptRunner.DashboardHost` é um painel pronto: login fixo por variáveis de ambiente
(`PythonScriptRunner__Dashboard__Login__Users__0__Name`, `__Role`, `__PasswordHash`) ou OpenID Connect com
`Oidc__Authority`, `Oidc__ClientId` e `Oidc__ClientSecret` (papéis pela claim `Dashboard__RoleClaim`, padrão `roles`, e
`Dashboard__Roles__Reader|Executor|Author|Admin`). Também aceita `Dashboard__PathPrefix` (padrão `/psr`),
`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` atrás de proxy e `PSR_AUTO_MIGRATE=true`.

## Requisitos

- .NET 10 SDK
- [uv](https://docs.astral.sh/uv/) no PATH (cria e cacheia um ambiente Python por conjunto de dependências)
- PostgreSQL 16+ apenas para o pacote de persistência e RabbitMQ 3.13+ para a fila (os testes usam Docker/Testcontainers)

## Estrutura

| Pasta | Conteúdo |
|-------|----------|
| `src/PythonScriptRunner.Abstractions` | contratos públicos (tipos de campo, `StageDefinition`, `RunResult`, `PsrFile`, interfaces) |
| `src/PythonScriptRunner.Core` | catálogos (código e pasta), validação, ambientes uv, executor de processos, runner |
| `src/PythonScriptRunner.EntityFrameworkCore` | PostgreSQL: catálogo versionado, histórico, arquivos, idempotência, outbox, auditoria, retenção, agendamentos e ciclo das execuções enfileiradas |
| `src/PythonScriptRunner.RabbitMQ` | topologia, relay do outbox, `IRunQueue`, comando público, eventos e assinaturas |
| `src/PythonScriptRunner.Worker` | execução dos passos enfileirados, *lease*, retry, cancelamento, cron |
| `src/PythonScriptRunner.Dashboard` | painel embutível: API interna, hub SignalR, modos de acesso (login fixo, filtro, host, local) |
| `src/PythonScriptRunner.Tool` | dotnet tool `psr` (`psr hash-password`) |
| `hosts` | `WorkerHost`, `DashboardHost` e `ServiceDefaults` (Serilog, OpenTelemetry, health checks) |
| `python/psrunner` | SDK Python (`@stage`, `Context`, `File`, `StageError`, `psrunner.testing`, CLI) |
| `schemas` | JSON Schemas do `stage.json`, do `*.pipeline.json` e das mensagens públicas |
| `tests` | testes .NET (unitários e de integração com Python real) e fixtures de trilha |

## Desenvolvimento

```bash
dotnet build
dotnet test --project tests/PythonScriptRunner.Core.Tests
dotnet test --project tests/PythonScriptRunner.EntityFrameworkCore.Tests   # precisa de Docker
dotnet test --project tests/PythonScriptRunner.RabbitMQ.Tests              # precisa de Docker
dotnet test --project tests/PythonScriptRunner.Worker.Tests                # precisa de Docker
dotnet test --project tests/PythonScriptRunner.Dashboard.Tests             # precisa de Docker

cd python/psrunner
uv sync
uv run pytest
```

O teste de desempenho do painel usa ~200 mil execuções; `PSR_PERF_RUNS_PER_DAY=50000` roda o volume médio completo (90 dias).
O documento OpenAPI do painel é comparado com `tests/PythonScriptRunner.Dashboard.Tests/Snapshots/openapi-v1.verified.json`;
depois de revisar uma mudança intencional, `PSR_ACCEPT_SNAPSHOTS=1` atualiza o snapshot.

## Licença

[MIT](LICENSE)
