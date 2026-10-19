# PythonScriptRunner

Biblioteca .NET 10 para executar Python de forma tipada, encadeável e observável: etapas (**Stages**)
com contrato de entrada/saída, trilhas (**Pipelines**), execução assíncrona com RabbitMQ, agendamento
cron e painel Vue opcional.

> Projeto em desenvolvimento. Esta versão entrega o **Core Runner** (execução síncrona de Stages) e o
> SDK Python **`psrunner`**.

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

## Requisitos

- .NET 10 SDK
- [uv](https://docs.astral.sh/uv/) no PATH (cria e cacheia um ambiente Python por conjunto de dependências)

## Estrutura

| Pasta | Conteúdo |
|-------|----------|
| `src/PythonScriptRunner.Abstractions` | contratos públicos (tipos de campo, `StageDefinition`, `RunResult`, `PsrFile`, interfaces) |
| `src/PythonScriptRunner.Core` | catálogos (código e pasta), validação, ambientes uv, executor de processos, runner |
| `python/psrunner` | SDK Python (`@stage`, `Context`, `File`, `StageError`, `psrunner.testing`, CLI) |
| `schemas` | JSON Schema do `stage.json` |
| `tests` | testes .NET (unitários e de integração com Python real) |

## Desenvolvimento

```bash
dotnet build
dotnet test --project tests/PythonScriptRunner.Core.Tests

cd python/psrunner
uv sync
uv run pytest
```

## Licença

[MIT](LICENSE)
