# PythonScriptRunner

Biblioteca .NET 10 para executar Python de forma tipada, encadeável e observável: etapas (**Stages**)
com contrato de entrada/saída, trilhas (**Pipelines**), execução assíncrona com RabbitMQ, agendamento
cron e painel Vue opcional.

> Projeto em desenvolvimento. Esta versão entrega o **Core Runner** (execução síncrona de Stages e de
> trilhas), o SDK Python **`psrunner`**, a **persistência em PostgreSQL** (catálogo versionado e histórico) e a
> **execução assíncrona** com RabbitMQ, worker e agendamento cron.

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
| `hosts` | `WorkerHost` e `ServiceDefaults` (Serilog, OpenTelemetry, health checks) |
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

cd python/psrunner
uv sync
uv run pytest
```

## Licença

[MIT](LICENSE)
