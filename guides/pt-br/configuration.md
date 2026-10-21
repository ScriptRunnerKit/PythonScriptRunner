# Referência de configuração

[English](../en/configuration.md) | Português

As opções são definidas em código, no callback de cada método `Add...`, ou carregadas da configuração. Os hosts prontos
carregam estas seções, e você pode fazer o mesmo na sua aplicação:

```csharp
var configuration = builder.Configuration;
builder.Services
    .AddPythonScriptRunner(o => configuration.GetSection("PythonScriptRunner").Bind(o))
    .AddEntityFrameworkCore(o =>
    {
        o.UseNpgsql(configuration.GetConnectionString("psr")!);
        configuration.GetSection("PythonScriptRunner:EntityFrameworkCore").Bind(o);
    })
    .AddRabbitMq(o =>
    {
        o.ConnectionString = configuration.GetConnectionString("rabbitmq");
        configuration.GetSection("PythonScriptRunner:RabbitMQ").Bind(o);
    })
    .AddWorker(o => configuration.GetSection("PythonScriptRunner:Worker").Bind(o));
```

Em variáveis de ambiente, as seções são separadas por `__`: `PythonScriptRunner__Worker__MaxConcurrency=8`. Tempos usam o
formato `hh:mm:ss` (`00:10:00`).

## Runner (`PythonScriptRunner`)

| Opção | Padrão | Descrição |
|-------|--------|-----------|
| `WorkRoot` | `%TEMP%/psr` | Pasta das pastas de trabalho e de resultado das tentativas. |
| `EnvironmentCacheRoot` | `%LOCALAPPDATA%/psr` | Pasta onde ficam em cache os ambientes Python e os arquivos de lock. |
| `UvPath` | `uv` | Caminho ou nome do executável `uv` (procurado no PATH). |
| `DefaultPythonVersion` | `3.12` | Versão do Python das Etapas que não declaram uma. |
| `DefaultTimeout` | 5 minutos | Tempo limite das Etapas que não declaram um. |
| `DefaultMemoryLimitMb` | `1024` | Limite de memória das Etapas que não declaram um. |
| `MaxFileBytes` | 50 MB | Tamanho máximo de um arquivo. |
| `MaxRunFilesBytes` | 200 MB | Tamanho máximo de todos os arquivos de uma execução. |
| `MaxLogBytes` | 1 MB | Bytes de stdout e stderr guardados por tentativa (o final). |
| `MaxConcurrentProcesses` | número de processadores | Processos Python rodando ao mesmo tempo neste processo. |
| `ThreadsPerProcess` | não definido | Threads das bibliotecas numéricas em cada processo. Sem valor: 1 no Linux quando o limite de memória cai no `RLIMIT_AS`, sem limite nos outros casos. Veja [Limites de memória](hosting.md#limites-de-memória). |
| `PackageIndexUrl` | PyPI | Índice de pacotes (pip `--index-url`). |
| `ExtraIndexUrls` | nenhum | Índices de pacotes extras (pip `--extra-index-url`). |
| `PsrunnerRequirement` | `psrunner==0.1.0` | O SDK instalado em cada ambiente: um requisito do pip ou o caminho de um wheel local. |
| `OrphanRetention` | 24 horas | Idade a partir da qual pastas de trabalho que sobraram são apagadas. |

Os segredos são lidos de `PythonScriptRunner:Secrets:<NOME>`.

## Persistência (`PythonScriptRunner:EntityFrameworkCore`)

| Opção | Padrão | Descrição |
|-------|--------|-----------|
| `Retention:Artifacts` | 7 dias | Por quanto tempo o conteúdo dos arquivos é guardado. |
| `Retention:Runs` | 90 dias | Por quanto tempo execuções, passos, tentativas, logs e metadados de arquivos são guardados. |
| `Retention:Audit` | 365 dias | Por quanto tempo os eventos de auditoria são guardados. |
| `Maintenance:Enabled` | `true` | Executa o job de manutenção (partições, retenção e limpezas) neste processo. |
| `Maintenance:Interval` | 1 hora | Com que frequência o job de manutenção roda. |
| `Maintenance:BatchSize` | `1000` | Linhas tratadas por lote pelo job de manutenção. |
| `IdempotencyWindow` | 24 horas | Por quanto tempo uma chave de idempotência devolve a mesma execução. |
| `LatestVersionCacheDuration` | 30 segundos | Por quanto tempo a consulta da "última versão publicada" fica em cache neste processo. |
| `UseDatabaseClock` | `true` | Usa o relógio do PostgreSQL para leases, esperas de retry, agendamentos e outbox, para que hosts com relógios diferentes concordem. |

A conexão é definida em código, com `o.UseNpgsql(connectionString)`.

## RabbitMQ (`PythonScriptRunner:RabbitMQ`)

| Opção | Padrão | Descrição |
|-------|--------|-----------|
| `ConnectionString` | nenhum | URI AMQP: `amqp://usuario:senha@host:5672/vhost`, ou `amqps://` para TLS. |
| `DeclareTopology` | `true` | Declara exchanges e filas na inicialização. Com `false`, a inicialização só confere se elas existem (para brokers administrados por um operador). |
| `RunOutboxRelay` | `true` | Publica o outbox a partir deste processo. Várias instâncias podem fazer isso. |
| `EnablePublicCommands` | `true` | Consome o comando público `StartPipelineRun`. |
| `InstanceId` | nome da máquina e id do processo | Dá nome às filas por instância deste processo. |
| `RelayInterval` | 250 ms | Com que frequência o relay procura mensagens quando o outbox está ocioso. |
| `RelayBatchSize` | `100` | Mensagens publicadas por lote do relay. |
| `DeliveryLimit` | `5` | Tentativas de entrega de um comando antes de ele ir para `psr.dead-letter`. |
| `StartupConnectTimeout` | 10 segundos | Quanto a inicialização espera pelo broker antes de continuar em segundo plano. |

## Worker (`PythonScriptRunner:Worker`)

| Opção | Padrão | Descrição |
|-------|--------|-----------|
| `WorkerId` | o id de instância do RabbitMQ | Identificador gravado nas tentativas que este worker executa. |
| `MaxConcurrency` | número de processadores | Passos executados ao mesmo tempo por este worker. |
| `LeaseDuration` | 60 segundos | Lease de uma tentativa em andamento; outro worker a recupera quando não é renovado. |
| `LeaseRenewInterval` | 15 segundos | Com que frequência o lease de uma tentativa em andamento é renovado. |
| `ReaperInterval` | 15 segundos | Com que frequência os leases vencidos são procurados. |
| `MaxLeaseRecoveries` | `3` | Recuperações de um passo após workers perdidos antes de ele falhar com `WorkerLost`. |
| `ShutdownGracePeriod` | 5 minutos | Quanto tempo os passos em andamento têm para terminar quando o host para. A plataforma precisa permitir pelo menos esse tempo. |
| `WorkFolderCleanupInterval` | 1 hora | Com que frequência as pastas de trabalho que sobraram são removidas. |
| `Scheduler:Enabled` | `true` | Dispara agendamentos a partir deste worker. Vários workers nunca disparam a mesma ocorrência. |
| `Scheduler:PollInterval` | 5 segundos | Com que frequência os agendamentos vencidos são procurados (a precisão do disparo). |
| `Scheduler:BatchSize` | `50` | Agendamentos disparados por transação. |

## Login fixo do painel (`PythonScriptRunner:Dashboard:Login`)

| Opção | Padrão | Descrição |
|-------|--------|-----------|
| `Users` | nenhum | Lista de `{ Name, Role, PasswordHash }`. |
| `SlidingExpiration` | 8 horas | Duração da sessão, renovada enquanto o usuário está ativo. |
| `AbsoluteExpiration` | 24 horas | Duração máxima da sessão. |
| `MaxFailedAttempts` | `5` | Falhas de login seguidas antes de o usuário ser bloqueado. |
| `LockoutDuration` | 15 minutos | Quanto tempo um usuário bloqueado espera. |
| `MaxAttemptsPerMinutePerIp` | `10` | Tentativas de login permitidas por IP por minuto. |

As mesmas opções existem em código no `UseFixedLogin(l => ...)`, que também tem o `PersistKeysInDatabase` (padrão
`true`): ele guarda as chaves do cookie no PostgreSQL, para que a sessão valha em todas as instâncias e sobreviva a
reinícios.
