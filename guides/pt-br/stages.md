# Etapas

[English](../en/stages.md) | Português

Uma Etapa é um script Python e o seu contrato. Esta página cobre o contrato, todas as opções do `stage.json`, a definição
de Etapas em C# e a leitura dos resultados no .NET. Para o lado do script, veja o [SDK Python](python-sdk.md).

## Estrutura de pastas

Com o catálogo em pasta (`AddFileCatalog("psr")`), cada Etapa é uma pasta dentro de `stages/`:

```text
psr/
  stages/
    media/
      stage.json         o contrato e as configurações
      script.py          o código (uma função marcada com @stage)
      requirements.txt   opcional: pacotes Python, um por linha
  pipelines/
    relatorio.pipeline.json
```

Com a persistência ligada, o catálogo passa a ficar no PostgreSQL, e a mesma pasta pode ser importada como zip (veja
[Persistência](persistence.md)).

## stage.json

```json
{
  "$schema": "https://raw.githubusercontent.com/ScriptRunnerKit/PythonScriptRunner/main/schemas/stage.v1.json",
  "key": "polinomio",
  "version": 1,
  "name": "Avaliar um polinômio",
  "description": "a + b·x + c·x² + … para cada x",
  "python": "3.12",
  "timeoutSeconds": 120,
  "memoryLimitMb": 2048,
  "retry": { "maxAttempts": 2, "initialDelaySeconds": 10, "backoffMultiplier": 2 },
  "idempotent": true,
  "secrets": ["SMTP_SENHA"],
  "inputs": [
    { "name": "coeficientes", "type": "list", "itemType": "number" },
    { "name": "x", "type": "list", "itemType": "number" }
  ],
  "outputs": [
    { "name": "valores", "type": "list", "itemType": "number" }
  ]
}
```

A linha `$schema` dá autocompletar e validação em editores como o VS Code.

| Propriedade | Obrigatória | Significado |
|-------------|-------------|-------------|
| `key` | sim | Identificador único: letras minúsculas e dígitos, com palavras separadas por um hífen (`relatorio-mensal`). É usado para executar a Etapa. |
| `version` | sim | Versão inteira. As trilhas referenciam `chave@versão`. |
| `name` | sim | Nome de exibição. |
| `description` | não | Texto livre exibido no painel. |
| `python` | não | Versão do Python (3.10 ou mais nova). Padrão: `DefaultPythonVersion`, que é 3.12. |
| `script` | não | Arquivo do script, relativo à pasta. Padrão: `script.py`. |
| `requirements` | não | Arquivo de requisitos, relativo à pasta. Padrão: `requirements.txt`, se existir. |
| `timeoutSeconds` | não | Tempo limite de uma tentativa. Padrão: `DefaultTimeout`, que é 5 minutos. |
| `memoryLimitMb` | não | Limite de memória de uma tentativa. Padrão: `DefaultMemoryLimitMb`, que é 1024. |
| `retry` | não | Nova tentativa automática depois de uma falha. Veja [Retry](#retry). |
| `idempotent` | não | `true` quando executar a Etapa duas vezes tem o mesmo efeito de uma. Permite que um worker a retome depois que outro worker caiu. Padrão: `false`. |
| `secrets` | não | Nomes dos segredos que o script recebe (maiúsculas, dígitos e `_`). Veja [Segredos](#segredos). |
| `inputs`, `outputs` | sim | Os campos do contrato. As listas podem ser vazias. |

## Tipos de campo

Cada entrada e saída tem um `name` e um `type`. A biblioteca converte os valores dos dois lados:

| Tipo | No .NET (entradas e `GetOutput<T>`) | No Python (`inputs[...]` e valor de retorno) |
|------|--------------------------------------|---------------------------------------------|
| `number` | `double` | `float` (`NaN` e infinito são rejeitados) |
| `integer` | `int`, `long` | `int` |
| `decimal` | `decimal` | `decimal.Decimal` (exato, enviado como texto) |
| `string` | `string` | `str` |
| `bool` | `bool` | `bool` |
| `date` | `DateOnly` | `datetime.date` |
| `datetime` | `DateTimeOffset` | `datetime.datetime` com fuso UTC (valores sem fuso são rejeitados) |
| `enum` | `string` de `enumValues` | `str` de `enumValues` |
| `json` | qualquer valor JSON (`JsonNode`, ou o seu próprio tipo) | `dict`, `list`, `str`, número, `bool` ou `None` |
| `table` | uma lista de registros (o seu próprio tipo de registro, ou `JsonArray`) | `list[dict]`, ou `pandas.DataFrame` com o extra `pandas` |
| `file` | `PsrFile` | `psrunner.File` (entrada); `ctx.new_file(...)` (saída) |
| `list` | arrays e listas do tipo do item | `list` do tipo do item |

Propriedades de um campo:

| Propriedade | Significado |
|-------------|-------------|
| `name` | Nome do campo: letras minúsculas, dígitos e `_`, começando por letra ou `_`. |
| `type` | Um dos tipos acima. |
| `itemType` | Tipo do item de uma `list` (qualquer tipo, exceto `list`). |
| `required` | Padrão `true`. Uma entrada opcional pode ser omitida; uma saída opcional pode faltar. |
| `default` | Só entradas: o valor usado quando a entrada não é informada. |
| `description` | Exibida no painel e no formulário de disparo gerado. |
| `enumValues` | Valores permitidos de um `enum` (ou de uma `list` de `enum`). |
| `contentTypes` | Tipos MIME permitidos de um `file`, por exemplo `["application/pdf"]`. |
| `columns` | Esquema opcional de uma `table`: `[{ "name": "x", "type": "number" }]`. Cada coluna tem um tipo escalar. |

## Pacotes Python

Coloque um `requirements.txt` ao lado do script, com um pacote por linha (`numpy`, `pandas>=2.2`). O runner cria, com o
uv, um ambiente por versão do Python e conjunto de requisitos, guarda em cache e o reaproveita em todas as Etapas com o
mesmo conjunto. Com a persistência, publicar uma Etapa trava os requisitos com hash (`uv pip compile --generate-hashes`),
então uma versão publicada sempre roda com as mesmas versões dos pacotes.

O SDK `psrunner` é instalado em todos os ambientes automaticamente. Não o coloque no `requirements.txt`.

## Definindo uma Etapa em C#

O mesmo contrato pode ser declarado em código, sem arquivos:

```csharp
builder.Services.AddPythonScriptRunner()
    .AddStage("media", s => s
        .Script("""
            from psrunner import stage

            @stage
            def run(inputs, ctx):
                v = inputs["valores"]
                return {"media": sum(v) / len(v)}
            """)
        .Python("3.12")
        .Requirements("numpy")
        .Input("valores", FieldType.ListOf(FieldType.Number))
        .Output("media", FieldType.Number)
        .Timeout(TimeSpan.FromMinutes(1))
        .MemoryLimitMb(512));
```

O `FieldType` tem `Number`, `Integer`, `Decimal`, `String`, `Bool`, `Date`, `DateTime`, `Json`, `Table`, `File`,
`Enum("a", "b")` e `ListOf(tipoDoItem)`. Uma definição inválida falha na inicialização com `PsrConfigurationException`.

## Executando uma Etapa e lendo o resultado

```csharp
await using var resultado = await runner.RunStageAsync("media", new { valores = new[] { 2.0, 4.0, 9.0 } });

if (resultado.Status == RunStatus.Succeeded)
{
    double media = resultado.GetOutput<double>("media");
}
else
{
    Console.WriteLine($"{resultado.Error!.Kind}: {resultado.Error.Message}");
}
```

- **Entradas:** um objeto anônimo, um dicionário ou qualquer objeto que serialize para o JSON esperado. Arquivos são
  passados como `PsrFile`: `PsrFile.FromPath(...)`, `FromStream`, `FromBytes` ou `FromText`.
- **Antes da execução:** entradas fora do contrato lançam `InputValidationException`, e nenhuma execução é criada.
- **Depois da execução:** falhas aparecem em `resultado.Status` e `resultado.Error`. `resultado.EnsureSuccess()` lança
  uma exceção se a execução não teve sucesso.
- **Leitura das saídas:**
  - `GetOutput<T>(nome)` lê um valor;
  - `GetFile(nome)` e `GetFiles(nome)` devolvem objetos `PsrFile` (`OpenReadAsync`, `SaveAsAsync`).
- **Descarte:** o `RunResult` é `IAsyncDisposable`. Descartá-lo apaga os arquivos de saída locais, então salve antes os
  que precisar (`SaveAsAsync`).
- **Opções:** o `RunOptions` escolhe uma `Version` específica, define uma `IdempotencyKey` (com persistência) e
  registra quem iniciou a execução.

Tipos de erro em `resultado.Error.Kind`:

| Tipo | Significado |
|------|-------------|
| `InputValidation` | Uma entrada não correspondeu ao contrato. |
| `OutputContractViolation` | O script devolveu algo fora do contrato (saída faltando, tipo errado, `NaN`). |
| `ScriptError` | O script lançou uma exceção, ou um `StageError` com um `code`. |
| `Timeout` | A tentativa passou do tempo limite. |
| `MemoryLimitExceeded` | A tentativa passou do limite de memória. |
| `Cancelled` | A execução foi cancelada. |
| `WorkerLost` | O worker que executava o passo parou de responder (execuções enfileiradas). |
| `EnvironmentSetupFailed` | O uv não conseguiu criar o ambiente (pacote inexistente, sem rede, versão do Python desconhecida). |
| `ArtifactLimitExceeded` | Os arquivos passaram de `MaxFileBytes` ou `MaxRunFilesBytes`. |
| `SecretNotFound` | Um segredo declarado não está configurado. O Python nem chega a iniciar. |
| `ProcessCrashed` | O processo Python não iniciou ou terminou sem um resultado. |
| `Expired` | A execução atingiu o limite de retenção antes de terminar. |

## Retry

O `retry` faz o runner tentar de novo depois de uma tentativa com falha:

- **`maxAttempts`:** quantas tentativas extras são permitidas (de 0 a 20). O padrão, 0, significa sem retry automático.
- **Espera antes de cada nova tentativa:** `initialDelaySeconds × backoffMultiplier^(n-1)`, com no máximo 10 minutos. Os
  padrões são 10 segundos e 2.

Uma execução cancelada nunca é repetida. As execuções enfileiradas aguardam a espera sem ocupar um worker.

## Segredos

A Etapa declara os nomes dos segredos de que precisa. Os valores vêm da configuração, na seção
`PythonScriptRunner:Secrets` (por exemplo, a variável de ambiente `PythonScriptRunner__Secrets__SMTP_SENHA`), ou de um
`ISecretProvider` seu. Os valores nunca são gravados nas entradas, nos logs, nas métricas nem no banco de dados. Só o
processo daquela Etapa os recebe, e eles aparecem mascarados (`***`) na saída capturada. No Python, leia com
`ctx.secrets["SMTP_SENHA"]`.
