# Persistência (PostgreSQL)

[English](../en/persistence.md) | Português

Com o `PythonScriptRunner.EntityFrameworkCore`, as Etapas e trilhas ficam no PostgreSQL com versões imutáveis, e toda
execução fica registrada. Este nível não precisa de RabbitMQ: as execuções síncronas também são registradas.

## Configuração

```bash
dotnet add package PythonScriptRunner.EntityFrameworkCore
```

```csharp
builder.Services
    .AddPythonScriptRunner()
    .AddEntityFrameworkCore(o =>
    {
        o.UseNpgsql(builder.Configuration.GetConnectionString("psr")!);   // PostgreSQL 16 ou mais novo
        o.Retention.Artifacts = TimeSpan.FromDays(7);                     // conteúdo dos arquivos
        o.Retention.Runs = TimeSpan.FromDays(90);                         // execuções, passos, tentativas e logs
    });
builder.Services.AddHealthChecks().AddPythonScriptRunnerDatabase();

var app = builder.Build();
await app.Services.MigratePythonScriptRunnerAsync();
```

O schema nunca é alterado de forma implícita. Há duas opções:
- **Na inicialização:** chame `MigratePythonScriptRunnerAsync()`. Instâncias simultâneas esperam num advisory lock do
  PostgreSQL, então as migrações rodam uma única vez.
- **Pelo seu DBA:** gere o SQL com `GeneratePythonScriptRunnerMigrationScriptAsync(idempotent: true)` e entregue a ele.

Tudo é criado no schema `psr`.

## O catálogo

Com persistência, o catálogo vem do banco de dados. Cada Etapa e trilha tem versões que passam de **rascunho** para
**publicada** e depois **arquivada**. Uma versão publicada nunca muda; editar cria um novo rascunho.

O `ICatalogAdmin` administra o catálogo. O painel usa o mesmo serviço.

```csharp
var admin = app.Services.GetRequiredService<ICatalogAdmin>();
var ator = Actor.User("ana@exemplo.com");

var rascunho = await admin.CreateStageAsync(new NewCatalogItem("media", "Média"), ator);
var salvo = await admin.SaveStageDraftAsync("media", rascunho.Version, rascunho.Data with
{
    Script = script,
    Inputs = [new FieldDefinition("valores", FieldType.ListOf(FieldType.Number))],
    Outputs = [new FieldDefinition("media", FieldType.Number)],
}, rascunho.RowVersion, ator);
await admin.PublishStageAsync("media", salvo.Version, salvo.RowVersion, ator);
```

| Operação | Métodos |
|----------|---------|
| Criar e editar rascunhos | `CreateStageAsync`, `CreateStageDraftFromAsync`, `SaveStageDraftAsync`, `DiscardStageDraftAsync` (e os equivalentes de `Pipeline`) |
| Validar e testar um rascunho | `ValidateStageDraftAsync`, `TestStageDraftAsync`, `EnqueueStageDraftTestAsync` (num worker) |
| Publicar | `PublishStageAsync`, `PublishPipelineAsync` |
| Arquivar e excluir | `ArchiveStageVersionAsync`, `DeleteStageAsync` (e os equivalentes de `Pipeline`) |
| Importar e exportar | `ImportAsync(zip)` cria rascunhos a partir de um zip no formato de pasta; `ExportStageVersionAsync` e `ExportPipelineVersionAsync` |
| Agendamentos | `CreateScheduleAsync`, `UpdateScheduleAsync`, `SetScheduleEnabledAsync`, `DeleteScheduleAsync` (veja [Fila, worker e agendamento](queue-and-worker.md)) |

Detalhes dessas operações:
- **Concorrência otimista:** os salvamentos usam a versão da linha, então duas pessoas editando o mesmo rascunho não
  sobrescrevem uma à outra sem perceber.
- **Publicar uma Etapa:**
  1. confere a sintaxe do script com o Python declarado;
  2. trava os requisitos com hash;
  3. congela a versão.
- **Publicar uma trilha:** valida os tipos e as ligações contra as Etapas publicadas.
- **Trazer uma pasta:** para levar um catálogo em pasta para o banco, compacte a pasta (com `stages/` e `pipelines/` na
  raiz), importe e depois publique as Etapas e as trilhas.

## Execuções

Toda execução fica gravada, seja síncrona (`IPythonRunner`), enfileirada, agendada ou disparada pelo painel. O registro
inclui as entradas, os passos, cada tentativa com duração e erro, o final do stdout e do stderr (até `MaxLogBytes`) e os
arquivos. O conteúdo dos arquivos fica no PostgreSQL (`bytea`), gravado e lido em streaming.

O `IRunManager` consulta e administra as execuções:

| Método | Descrição |
|--------|-----------|
| `GetAsync(runId)` | A execução completa, com passos e tentativas. |
| `ListAsync(query)` | Execuções filtradas por status, trilha, Etapa, origem ou período, com paginação keyset. |
| `GetAttemptLogAsync(runId, attemptId, stream)` | O stdout ou stderr capturado de uma tentativa. |
| `CancelAsync(runId, ator)` | Cancela uma execução. Uma execução enfileirada para no worker que a executa. |
| `ReprocessAsync(runId, ator, mode)` | Repete uma execução que falhou, com a mesma versão e as mesmas entradas. Os passos que deram certo mantêm as saídas e o passo que falhou ganha uma nova tentativa. `ExecutionMode.Async` (o padrão) enfileira para os workers e precisa do RabbitMQ; `ExecutionMode.Sync` executa agora, no processo atual. |

**Idempotência:** o `RunOptions.IdempotencyKey` devolve a mesma execução quando a mesma chave é usada de novo dentro do
`IdempotencyWindow` (24 horas por padrão). É útil quando quem chama pode repetir uma requisição.

## Retenção

O histórico é particionado: execuções por semana, conteúdo dos arquivos por dia e auditoria por mês. Um job de
manutenção, ligado por padrão e executado a cada hora, cria as partições com antecedência e remove as vencidas, o que é
barato mesmo com grandes volumes.

| Opção | Padrão |
|-------|--------|
| `Retention.Artifacts` (conteúdo dos arquivos) | 7 dias |
| `Retention.Runs` (execuções, passos, tentativas, logs e metadados de arquivos) | 90 dias |
| `Retention.Audit` | 365 dias |
| `Maintenance.Enabled`, `Maintenance.Interval` | `true`, 1 hora |

Uma trilha pode ter retenção menor que a global, mas não maior.

## Auditoria

As ações humanas ficam em `psr.audit_events`: quem fez o quê, em qual item e quando. Isso cobre edição, publicação,
arquivamento, exclusão, cancelamento, reprocessamento, agendamento e mudanças de configuração. O autor de cada ação é um
`Actor`:
- `Actor.User("identificador")` para pessoas;
- `Actor.System("origem")` para disparos automáticos, que ficam registrados na própria execução.
