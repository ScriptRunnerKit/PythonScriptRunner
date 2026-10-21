# Primeiros passos

[English](../en/getting-started.md) | Português

O PythonScriptRunner liga código Python a aplicações .NET. O cálculo fica num script Python comum, escrito por quem
domina o assunto, e o .NET recebe um resultado já validado contra um contrato.

## Conceitos

- **Etapa**: um script Python com um **contrato**, a lista de entradas e saídas e seus tipos. O script implementa uma
  única função, `run(inputs, ctx)`; a biblioteca valida o que entra e o que sai.
- **Trilha**: uma sequência ordenada de Etapas. A saída de um passo pode alimentar a entrada de um passo seguinte, e os
  tipos de todas as ligações são conferidos antes de qualquer execução.
- **Execução**: cada vez que uma Etapa ou trilha roda. Cada tentativa roda num processo Python próprio, com timeout,
  limite de memória e ambiente Python próprio.

## Formas de uso

Comece pela configuração mínima que resolve o seu caso. O código de um nível continua válido no seguinte.

| Nível | O que você precisa | O que ganha |
|-------|--------------------|-------------|
| **1. Somente a biblioteca** | .NET 10 e uv | Executar Etapas e trilhas a partir do seu código. Sem banco de dados, sem broker de mensagens e sem painel. |
| **2. Com PostgreSQL** | PostgreSQL 16 ou mais novo | Catálogo versionado (rascunho, publicada e arquivada), histórico de todas as execuções, arquivos e auditoria. Veja [Persistência](persistence.md). |
| **3. Com RabbitMQ** | PostgreSQL e RabbitMQ 3.13 ou mais novo | Execução em segundo plano por workers, retry automático, agendamento por cron, eventos e um comando para sistemas escritos em outras linguagens. Veja [Fila, worker e agendamento](queue-and-worker.md). |
| **4. Com o painel** | PostgreSQL e RabbitMQ | Uma interface web embutida na sua aplicação para editar, testar, executar e acompanhar Etapas e trilhas. Veja [Painel](dashboard.md). |
| **Hosts prontos** | Docker | Imagens de contêiner do worker e do painel, ou o ambiente completo com um comando (.NET Aspire). Veja [Hospedagem](hosting.md). |

## Pré-requisitos

1. **SDK do .NET 10**: <https://dotnet.microsoft.com/download>
2. **uv**, que cria e mantém em cache os ambientes Python. Você não precisa instalar o Python: o uv baixa a versão que
   cada Etapa pede.
   - Windows: `winget install --id=astral-sh.uv -e`
   - macOS e Linux: `curl -LsSf https://astral.sh/uv/install.sh | sh`

## Pacotes

| Pacote | Uso |
|--------|-----|
| `PythonScriptRunner.Core` | Executar Etapas e trilhas (nível 1). Traz o `PythonScriptRunner.Abstractions`. |
| `PythonScriptRunner.EntityFrameworkCore` | Persistência no PostgreSQL (nível 2) |
| `PythonScriptRunner.RabbitMQ` | Fila, relay do outbox, comando público e eventos (nível 3) |
| `PythonScriptRunner.Worker` | Executa os passos enfileirados e os agendamentos (nível 3) |
| `PythonScriptRunner.Dashboard` | Interface web embutida (nível 4) |
| `PythonScriptRunner.Tool` | A ferramenta de linha de comando `psr` (`psr hash-password` para o login do painel) |
| `psrunner` (PyPI) | O SDK Python. O runner o instala em cada ambiente; instale você mesmo só para testar scripts localmente. |

Todos os pacotes NuGet têm a mesma versão. Veja a [tabela de compatibilidade](../../README.md#compatibility).

## Sua primeira Etapa

Crie um projeto de console:

```bash
dotnet new console -n MeuCalculo
cd MeuCalculo
dotnet add package PythonScriptRunner.Core
dotnet add package Microsoft.Extensions.Hosting
```

Crie uma pasta `psr/stages/media` com dois arquivos.

`psr/stages/media/stage.json`, o contrato:

```json
{
  "key": "media",
  "version": 1,
  "name": "Média",
  "inputs":  [ { "name": "valores", "type": "list", "itemType": "number" } ],
  "outputs": [ { "name": "media", "type": "number" } ]
}
```

`psr/stages/media/script.py`, o código:

```python
from psrunner import stage

@stage
def run(inputs, ctx):
    valores = inputs["valores"]
    ctx.log.info("calculando a média de %d valores", len(valores))
    return {"media": sum(valores) / len(valores)}
```

`Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PythonScriptRunner;

var builder = Host.CreateApplicationBuilder(args);
builder.Services
    .AddPythonScriptRunner()
    .AddFileCatalog("psr");                 // pasta com stages/ e pipelines/

using var host = builder.Build();
await host.StartAsync();                    // confere a configuração (uv no PATH, catálogo)

var runner = host.Services.GetRequiredService<IPythonRunner>();
await using var resultado = await runner.RunStageAsync("media", new { valores = new[] { 2.0, 4.0, 9.0 } });
resultado.EnsureSuccess();
Console.WriteLine(resultado.GetOutput<double>("media"));   // 5
```

Rode com `dotnet run`. A primeira execução leva alguns segundos a mais porque o uv cria o ambiente Python; as seguintes
o reaproveitam.

O que aconteceu:
- As entradas foram conferidas contra o contrato antes de o Python iniciar.
- O script rodou num processo separado, num ambiente limpo criado pelo uv.
- A saída foi conferida contra o contrato e convertida para `double`.

Se o script devolver algo fora do contrato (um texto onde se espera um número, ou `NaN`), a execução falha com um erro
claro em vez de entregar dados inválidos.

## Próximos passos

- Adicionar bibliotecas, limites e segredos a uma Etapa, ou defini-la em C#: [Etapas](stages.md).
- Encadear Etapas: [Trilhas](pipelines.md).
- Testar scripts com pytest, sem .NET: [SDK Python](python-sdk.md).
- Guardar histórico e versões: [Persistência](persistence.md).
