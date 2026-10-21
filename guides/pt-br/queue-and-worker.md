# Fila, worker e agendamento (RabbitMQ)

[English](../en/queue-and-worker.md) | Português

Com o `PythonScriptRunner.RabbitMQ` e o `PythonScriptRunner.Worker`, as execuções acontecem em segundo plano. A sua
aplicação enfileira uma execução e segue; os workers, numa máquina ou em várias, executam os passos. Este nível precisa
da [persistência](persistence.md): o PostgreSQL guarda o estado de cada execução e o outbox das mensagens.

## Configuração

```bash
dotnet add package PythonScriptRunner.EntityFrameworkCore
dotnet add package PythonScriptRunner.RabbitMQ
dotnet add package PythonScriptRunner.Worker
```

```csharp
builder.Services
    .AddPythonScriptRunner()
    .AddEntityFrameworkCore(o => o.UseNpgsql(builder.Configuration.GetConnectionString("psr")!))
    .AddRabbitMq(o => o.ConnectionString = builder.Configuration.GetConnectionString("rabbitmq"))   // RabbitMQ 3.13 ou mais novo
    .AddWorker(o => o.MaxConcurrency = 4);
builder.Services.AddHealthChecks().AddPythonScriptRunnerChecks();   // banco, broker e uv
```

O `AddWorker` vai só nos processos que devem executar passos. Uma aplicação web pode enfileirar execuções e mostrar o
andamento só com o `AddRabbitMq`, enquanto processos de worker dedicados (ou a [imagem do worker](hosting.md)) fazem o
trabalho.

## Enfileirando uma execução

```csharp
var fila = app.Services.GetRequiredService<IRunQueue>();

RunHandle execucao = await fila.EnqueuePipelineAsync("calculo-progressao",
    new { a1 = 1, razao = 2, n = 4, coeficientes = new[] { 1.0, 0.0, 1.0 } },
    new RunOptions { IdempotencyKey = "pedido-1234" });
```

O `EnqueuePipelineAsync` e o `EnqueueStageAsync` validam as entradas, registram a execução e devolvem um handle com o id
dela. Acompanhe pelo `IRunManager.GetAsync(execucao.RunId)`, pelos [eventos](#eventos) ou pelo [painel](dashboard.md).

## O caminho de uma execução

1. A execução e a mensagem do primeiro passo são gravadas numa única transação do PostgreSQL (um outbox). Enfileirar
   funciona mesmo com o RabbitMQ fora do ar; a mensagem é enviada quando o broker volta.
2. Um worker assume o passo com um **lease**, executa o processo Python e renova o lease enquanto ele roda.
3. Quando o passo termina, o worker grava o resultado e a mensagem do próximo passo na mesma transação.
4. Os eventos da execução são publicados conforme ela avança.

## Confiabilidade

- **Retry:** uma tentativa com falha é repetida conforme a política `retry` da Etapa (veja [Etapas](stages.md#retry)). A
  execução aguarda a espera sem ocupar um worker.
- **Workers perdidos:** se um worker para de renovar o lease (caiu, ou a máquina sumiu), o passo é recuperado.
  - Uma Etapa marcada como `idempotent`, ou que ainda tem retries, é retomada por outro worker.
  - Nos outros casos, o passo falha com `WorkerLost`, para reprocesso manual.
- **Duplicatas:** uma mensagem entregue duas vezes nunca executa um passo duas vezes.
- **Mensagens problemáticas:** depois de `DeliveryLimit` entregas com falha (5), um comando vai para a fila
  `psr.dead-letter`.
- **Desligamento gradual:** um worker que está parando não pega passos novos e espera até `ShutdownGracePeriod` (5
  minutos) pelos que estão rodando.

## Concorrência e cancelamento

- **Por worker:** o `MaxConcurrency` limita quantos passos um worker executa ao mesmo tempo. O padrão é o número de
  processadores.
- **Por trilha:** dá para definir um máximo de execuções simultâneas. As execuções além do limite esperam como
  `Pending`, em ordem.
- **Cancelamento:** o `IRunManager.CancelAsync` encerra a árvore de processos no worker que executa o passo, onde quer
  que ele esteja.

## Agendamento

Os agendamentos executam uma trilha a partir de uma expressão cron, num fuso IANA:

```csharp
var admin = app.Services.GetRequiredService<ICatalogAdmin>();
await admin.CreateScheduleAsync("calculo-progressao", new ScheduleData
{
    Name = "Todo dia útil às 2h",
    Cron = "0 2 * * 1-5",                  // 5 campos, ou 6 com segundos
    TimeZone = "America/Sao_Paulo",        // o padrão; o horário de verão é tratado
    Inputs = new JsonObject { ["a1"] = 1, ["razao"] = 2, ["n"] = 4, ["coeficientes"] = new JsonArray(1, 0, 1) },
}, Actor.User("ana@exemplo.com"));
```

Como os agendamentos se comportam:
- **Versão:** sem `PinnedVersion`, cada disparo executa a última versão publicada.
- **Disparo único:** vários workers nunca disparam a mesma ocorrência.
- **Ocorrências perdidas:** se foram perdidas enquanto todos os workers estavam fora do ar, viram uma única execução de
  recuperação.
- **Entradas de arquivo:** uma trilha com entrada `file` obrigatória não pode ser agendada.
- **Opções do agendador:** ele roda dentro dos workers, em `Scheduler` nas opções do worker (`Enabled`, `PollInterval`,
  `BatchSize`).

## Eventos

Toda execução publica eventos na exchange tópica `psr.events`:

| Evento | Quando |
|--------|--------|
| `run.queued`, `run.rejected`, `run.started` | A execução foi aceita, recusada (por exemplo, entradas inválidas) ou iniciada. |
| `step.started`, `step.succeeded`, `step.failed`, `step.retry-scheduled` | O andamento de cada passo. |
| `run.succeeded`, `run.failed`, `run.cancelled` | A execução terminou. O `run.succeeded` leva as saídas. |

A routing key é `<evento>.<chave da trilha>` (ou `<evento>.stage:<chave da etapa>` nas execuções de uma Etapa só), então
dá para assinar exatamente o que você precisa. Dentro do .NET, trate os eventos com `IRunEventHandler`:

```csharp
builder.Services.AddPythonScriptRunner()
    .AddRabbitMq(o => o.ConnectionString = builder.Configuration.GetConnectionString("rabbitmq"))
    .AddRunEventHandler<NotificarErp>(s => s
        .Subscription("erp")                                   // uma fila durável compartilhada por todas as instâncias
        .On("run.succeeded.calculo-progressao", "run.failed.*"));

public sealed class NotificarErp : IRunEventHandler
{
    public Task HandleAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
        // runEvent.Type, runEvent.RunId, runEvent.Outputs, runEvent.Error, ...
        return Task.CompletedTask;
    }
}
```

Lançar uma exceção no `HandleAsync` faz o broker entregar o evento de novo, até o limite de entregas. Use
`PerInstance(prefixo)` no lugar de `Subscription(nome)` quando cada instância precisar receber todos os eventos.

## Sistemas escritos em outras linguagens

Qualquer sistema que fale com o RabbitMQ pode iniciar trilhas e acompanhá-las, sem .NET:

- **Iniciar uma execução:**
  - publique uma mensagem JSON `StartPipelineRun` na exchange `psr.public`, com a routing key `pipeline.start`;
  - campos: `pipelineKey` (obrigatório), `version`, `inputs`, `idempotencyKey`, `correlationId` e `requestedBy`;
  - entradas de arquivo não são aceitas por esse comando.
- **Acompanhar:** ligue uma fila sua à exchange `psr.events` com as routing keys que quiser, por exemplo
  `run.*.calculo-progressao`. O `correlationId` que você enviou volta em todos os eventos.

Os contratos das mensagens são JSON Schemas em [`schemas/messages`](../../schemas/messages):
`start-pipeline-run.v1.json` e `run-event.v1.json`.

## Observabilidade

O `traceparent` acompanha a execução de quem chamou até o worker, então um único trace distribuído mostra a requisição,
a fila e cada processo Python. Veja [Como funciona](how-it-works.md#observabilidade).
