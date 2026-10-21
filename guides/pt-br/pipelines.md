# Trilhas

[English](../en/pipelines.md) | Português

Uma trilha executa Etapas uma depois da outra. Cada passo pode usar as entradas da trilha, as saídas de qualquer passo
anterior ou valores fixos, e a trilha declara as próprias saídas. Tipos e ligações são validados quando o catálogo é
carregado (com persistência, quando uma versão é publicada), então uma trilha que não consegue rodar é rejeitada antes da
execução.

## O arquivo da trilha

Com o catálogo em pasta, as trilhas são arquivos `pipelines/<nome>.pipeline.json`:

```json
{
  "$schema": "https://raw.githubusercontent.com/ScriptRunnerKit/PythonScriptRunner/main/schemas/pipeline.v1.json",
  "key": "calculo-progressao",
  "version": 1,
  "name": "Cálculo de progressão",
  "inputs": [
    { "name": "a1", "type": "number" },
    { "name": "razao", "type": "number" },
    { "name": "n", "type": "integer" },
    { "name": "coeficientes", "type": "list", "itemType": "number" }
  ],
  "steps": [
    { "key": "termos", "stage": "progressao-termos@1" },
    { "key": "polinomio", "stage": "polinomio@1",
      "bindings": { "coeficientes": "$pipeline.coeficientes", "x": "$steps.termos.termos" } },
    { "key": "estatisticas", "stage": "estatisticas@1",
      "bindings": { "valores": "$steps.polinomio.valores", "precisao": { "const": 4 } } }
  ],
  "outputs": [
    { "name": "soma", "source": "$steps.estatisticas.soma" },
    { "name": "termos", "source": "$steps.termos.termos", "description": "Termos da progressão" }
  ]
}
```

| Propriedade | Significado |
|-------------|-------------|
| `key`, `version`, `name`, `description` | Mesmas regras de uma Etapa. |
| `inputs` | Campos da trilha, com os mesmos tipos e propriedades dos campos de uma Etapa. |
| `steps` | Os passos, na ordem de execução. Cada um tem uma `key` (identificador), uma `stage` (`chave-da-etapa@versão`), um `displayName` opcional e `bindings` opcionais. |
| `outputs` | O que a trilha devolve. Cada saída tem um `name` e uma `source` (`$steps.<passo>.<campo>`); o tipo vem da saída daquele passo. |

## Ligações

Cada entrada de um passo recebe o valor de uma de três fontes:

| Ligação | Exemplo | Valor |
|---------|---------|-------|
| Entrada da trilha | `"$pipeline.coeficientes"` | A entrada da trilha com esse nome. |
| Passo anterior | `"$steps.termos.termos"` | A saída `termos` do passo `termos`. Vale qualquer passo anterior, não só o imediatamente anterior. |
| Constante | `{ "const": 4 }` | Um valor fixo. |

Uma entrada sem ligação explícita usa a **ligação implícita por nome**:
- no primeiro passo, a entrada da trilha com o mesmo nome;
- nos demais passos, a saída com o mesmo nome do passo anterior.

Quando os tipos são compatíveis mas não iguais (um `integer` alimentando um `decimal`, por exemplo), o valor é
convertido. Uma ligação incompatível é um erro de validação.

## Executando uma trilha

```csharp
await using var resultado = await runner.RunPipelineAsync("calculo-progressao",
    new { a1 = 1, razao = 2, n = 4, coeficientes = new[] { 1.0, 0.0, 1.0 } });

resultado.EnsureSuccess();
double soma = resultado.GetOutput<double>("soma");
double[] termos = resultado.GetStepOutput<double[]>("termos", "termos");   // qualquer saída de passo, útil para depuração
```

O `resultado.Steps` lista cada passo com status, tentativas, duração e erro.

Se um passo falhar, os passos seguintes terminam como `Skipped` e a execução termina como `Failed`, com o erro do passo
que falhou.
- **Execuções síncronas:** você inicia uma nova execução.
- **Com persistência:** o `IRunManager.ReprocessAsync` repete só a partir do passo que falhou, reaproveitando as saídas
  dos passos que deram certo. Veja [Persistência](persistence.md).

## Definindo uma trilha em C#

```csharp
builder.Services.AddPythonScriptRunner()
    .AddPipeline("calculo-progressao", p => p
        .Name("Cálculo de progressão")
        .Input("a1", FieldType.Number)
        .Input("razao", FieldType.Number)
        .Input("n", FieldType.Integer)
        .Input("coeficientes", FieldType.ListOf(FieldType.Number))
        .Step("termos", "progressao-termos@1")
        .Step("polinomio", "polinomio@1", s => s
            .Bind("coeficientes", new PipelineInputSource("coeficientes"))
            .Bind("x", new StepOutputSource("termos", "termos")))
        .Step("estatisticas", "estatisticas@1", s => s
            .Bind("valores", new StepOutputSource("polinomio", "valores"))
            .Bind("precisao", new ConstantSource(JsonValue.Create(4))))
        .Output("soma", StepOutputSource.Step("estatisticas", "soma")));
```

## Configurações das execuções enfileiradas

Com a fila (veja [Fila, worker e agendamento](queue-and-worker.md)), cada trilha também tem:
- **Máximo de execuções simultâneas:** as execuções além do limite esperam como `Pending`, em ordem.
- **Retenção:** ela só pode ser menor que a retenção global.

As duas são alteradas no painel ou com `ICatalogAdmin.UpdatePipelineSettingsAsync` e `UpdatePipelineRetentionAsync`.
