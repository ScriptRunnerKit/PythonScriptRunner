# Como funciona

[English](../en/how-it-works.md) | Português

Esta página explica o que acontece quando uma Etapa roda, o que a biblioteca garante e como observar tudo em produção.

## Uma tentativa, passo a passo

1. **Validar as entradas** contra o contrato. Entradas inválidas param aqui, e nenhum processo Python inicia.
2. **Resolver os segredos** que a Etapa declara. Um segredo faltando faz a tentativa falhar com `SecretNotFound`.
3. **Preparar o ambiente.** O uv cria (ou reaproveita) o ambiente Python da Etapa. Os ambientes são identificados por um
   hash da versão do Python, dos requisitos travados e da versão do `psrunner`. Um lock em arquivo faz execuções
   simultâneas esperarem uma única criação, e um ambiente só é usado depois de completamente instalado.
4. **Esperar uma vaga.** No máximo `MaxConcurrentProcesses` processos Python rodam ao mesmo tempo num processo .NET.
5. **Iniciar o processo** numa pasta só dele, com:
   - um ambiente limpo: só as variáveis de que o Python precisa, nunca as variáveis da sua aplicação;
   - os segredos declarados;
   - as entradas num arquivo.
6. **Aplicar os limites:** o tempo limite e o limite de memória (veja [Limites de memória](hosting.md#limites-de-memória)).
   No timeout ou no cancelamento, a árvore de processos inteira é encerrada.
7. **Capturar a saída.** O final do stdout e do stderr é guardado (até `MaxLogBytes`), com os valores dos segredos
   mascarados como `***`.
8. **Validar as saídas** contra o contrato e convertê-las para valores .NET. Os arquivos de saída são movidos para a
   execução, dentro de `MaxFileBytes` e `MaxRunFilesBytes`.

O lado .NET e o `psrunner` trocam arquivos no protocolo versão 1, e o `psrunner` continua compatível com ele enquanto
esse protocolo estiver em uso.

## Garantias

- **Isolamento:** cada tentativa roda no próprio processo, então uma falha grave no Python nunca derruba a aplicação .NET.
- **Limites:** toda tentativa tem tempo limite e limite de memória.
- **Segredos:** chegam só ao processo da Etapa que os declara. Nunca são gravados no arquivo de entradas, em logs,
  métricas, exceções ou no banco de dados.
- **Reprodutibilidade:** as dependências publicadas são travadas com hash e instaladas com `--require-hashes`, então uma
  versão publicada sempre roda com os mesmos pacotes.
- **Resultados tipados:** o lado .NET recebe valores de acordo com o contrato, ou um erro claro.

## Modelo de segurança

O PythonScriptRunner executa código escrito por **autores confiáveis**. Uma Etapa é código Python arbitrário rodando com
as permissões do usuário do worker; ela não é uma sandbox para código não confiável. Quem tem o papel `Author` no painel
pode publicar código que roda nos workers, então dê esse papel só a quem você deixaria fazer deploy de código.

Recomendações:
- Rode os workers com um usuário sem privilégios e com acesso só ao que as Etapas precisam. As imagens já rodam com um
  usuário não-root e com o sistema de arquivos raiz somente leitura.
- Guarde os segredos no cofre de segredos da sua plataforma.
- Sirva o painel por HTTPS.
- Não exponha a configuração de desenvolvimento do AppHost nem do compose gerado.

Para reportar uma vulnerabilidade, veja o [SECURITY.md](../../SECURITY.md).

## Observabilidade

**Traces.** O `ActivitySource` chamado `PythonScriptRunner` cria estes spans:
- `psr.run.enqueue`: uma execução foi enfileirada;
- `psr.message.consume`: uma mensagem foi tratada;
- `psr.environment.ensure`: um ambiente foi preparado;
- `psr.stage.execute`: uma tentativa rodou.

O `traceparent` viaja com as mensagens, então um único trace vai da requisição HTTP até o worker e cada processo Python.

![Trace de uma execução](../images/aspire-trace.png)

**Métricas.** O `Meter` chamado `PythonScriptRunner` publica estes instrumentos:

| Instrumento | Significado |
|-------------|-------------|
| `psr.step.duration` | Duração de cada tentativa. |
| `psr.step.overhead` | Tempo gasto pelo runner fora do processo Python. |
| `psr.step.failures` | Tentativas com falha. |
| `psr.environment.created` | Ambientes Python criados. |
| `psr.queue.wait` | Tempo entre enfileirar um passo e ele começar num worker. |
| `psr.worker.active_steps` | Passos rodando neste worker. |
| `psr.lease.recovered` | Tentativas reiniciadas depois que o worker delas foi perdido. |
| `psr.schedule.fired` | Execuções criadas por agendamentos. |
| `psr.outbox.publish.duration` | Tempo para publicar um lote do outbox. |
| `psr.maintenance.partitions.created`, `psr.maintenance.partitions.dropped`, `psr.maintenance.rows.deleted` | Trabalho do job de manutenção. |

Inclua a fonte e o meter na sua configuração do OpenTelemetry:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("PythonScriptRunner"))
    .WithMetrics(m => m.AddMeter("PythonScriptRunner"));
```

**Health checks:**
- o `AddPythonScriptRunnerDatabase()` confere o PostgreSQL;
- o `AddPythonScriptRunnerBroker()` confere o RabbitMQ;
- o `AddPythonScriptRunnerChecks()` confere o banco, o broker e o uv juntos.

**Logs.** A biblioteca registra pelo `ILogger`. Os comandos SQL são registrados em `Debug`, porque o relay do outbox
consulta o banco várias vezes por segundo.
