# SDK Python (psrunner)

[English](../en/python-sdk.md) | Português

O `psrunner` é o lado Python do PythonScriptRunner. Ele lê as entradas, converte para tipos Python, chama a sua função,
confere as saídas contra o contrato e as devolve ao .NET. O script contém só a lógica.

O runner instala o `psrunner` em cada ambiente que cria, então uma Etapa nunca o coloca no `requirements.txt`. Instale
você mesmo só para escrever e testar scripts localmente:

```bash
pip install psrunner             # Python 3.10 ou mais novo
pip install "psrunner[pandas]"   # entradas table chegam como pandas.DataFrame
```

## Escrevendo uma Etapa

```python
from psrunner import stage, StageError

@stage
def run(inputs, ctx):
    valores = inputs["valores"]
    if not valores:
        raise StageError("Informe pelo menos um valor.", code="ENTRADA_VAZIA")

    ctx.log.info("calculando a média de %d valores", len(valores))
    return {"media": sum(valores) / len(valores)}
```

As regras:
- **Um ponto de entrada:** exatamente uma função por script é marcada com `@stage`. Ela recebe `inputs` (um dict com as
  entradas, já convertidas) e `ctx` (o contexto descrito abaixo).
- **Valor de retorno:** ela devolve um dict com as saídas. Saídas obrigatórias precisam estar presentes; saídas fora do
  contrato são um erro.
- **Tipos:** os valores são convertidos como descrito em [Tipos de campo](stages.md#tipos-de-campo). Por exemplo,
  `decimal` chega como `Decimal`, e `datetime` precisa ter fuso UTC.

## O contexto (`ctx`)

| Membro | Descrição |
|--------|-----------|
| `ctx.log` | Um `logging.Logger`. As linhas aparecem nos logs da execução (painel e `RunResult`). `print` também funciona e vai para o stdout. |
| `ctx.secrets` | Mapeamento somente leitura com os segredos que a Etapa declara: `ctx.secrets["SMTP_SENHA"]`. |
| `ctx.new_file(nome, content_type=None)` | Reserva um arquivo de saída e devolve um `File`; escreva em `arquivo.path`. |
| `ctx.run_id`, `ctx.step_key`, `ctx.attempt` | Identificam a execução, o passo da trilha e o número da tentativa. |
| `ctx.stage_key`, `ctx.stage_version` | A Etapa em execução. |
| `ctx.is_test` | `True` nas execuções de teste de um rascunho. |
| `ctx.work_dir`, `ctx.out_dir`, `ctx.tmp_dir` | As pastas da própria tentativa. Use `tmp_dir` para arquivos temporários. |

## Arquivos

Uma entrada `file` chega como `psrunner.File`:

| Membro | Descrição |
|--------|-----------|
| `path` | Caminho local do arquivo. |
| `name`, `content_type`, `size`, `sha256` | Metadados. |
| `read_bytes()`, `read_text(encoding="utf-8")`, `open(mode="rb")` | Leem o conteúdo. |

Para produzir um arquivo de saída, reserve com `ctx.new_file` e devolva o `File`:

```python
@stage
def run(inputs, ctx):
    relatorio = ctx.new_file("relatorio.csv", "text/csv")
    relatorio.path.write_text("x,y\n1,2\n", encoding="utf-8")
    return {"relatorio": relatorio}
```

Os nomes de arquivo precisam ser simples, sem pastas. O runner aplica `MaxFileBytes` por arquivo e `MaxRunFilesBytes`
por execução (veja a [Configuração](configuration.md)).

## Erros

- **Erros de negócio:** lance `StageError(mensagem, code="ALGUM_CODIGO", details={...})`. A execução falha com o tipo
  `ScriptError` e esse `code`, para que o lado .NET e o painel possam reagir.
- **Exceções inesperadas:** qualquer outra exceção também vira `ScriptError`. O erro traz o traceback do seu script,
  que o painel mostra.
- **Violações de contrato:** uma saída faltando, um tipo errado ou um número não finito vira `OutputContractViolation`.

## Testes com pytest

O `psrunner.testing.run_stage` executa a função com as mesmas conversões e validações do runner real, sem .NET:

```python
from psrunner.testing import run_stage

def test_media():
    resultado = run_stage(
        "psr/stages/media/script.py",
        {"valores": [2, 4, 9]},
        stage_json="psr/stages/media/stage.json",
    )
    assert resultado.ok
    assert resultado.outputs == {"media": 5.0}
```

| Parâmetro | Descrição |
|-----------|-----------|
| `script` | Caminho do script, ou a própria função. |
| `inputs` | Dict com as entradas. |
| `stage_json` | Caminho do contrato. Com ele, entradas e saídas são validadas; sem ele, as entradas passam como estão. |
| `secrets` | Dict com os valores dos segredos. |
| `files` | Associa campos de arquivo a caminhos locais (uma lista para `list` de `file`). |
| `work_dir`, `run_id` | Pasta e id de execução opcionais. |

O resultado tem estes campos:
- `ok`: `True` quando a Etapa teve sucesso;
- `outputs`: os valores Python;
- `wire_outputs`: o JSON enviado ao .NET;
- `error`: com `kind`, `code`, `message` e `traceback`;
- `logs`: as linhas de log;
- `work_dir`: a pasta usada pela execução.

## Linha de comando

```bash
python -m psrunner validate psr/stages/media
python -m psrunner run psr/stages/media --input exemplo.json --secret SMTP_SENHA=segredo --keep
```

| Comando | O que faz |
|---------|-----------|
| `validate <pasta>` | Confere o `stage.json` e se o script tem exatamente uma função `@stage`. |
| `run <pasta>` | Executa a Etapa localmente com o protocolo real, num processo separado e numa pasta temporária. |

No `exemplo.json` do `run`, uma entrada de arquivo é escrita como `{"$path": "relativo/ao/exemplo.json"}`. O `--keep`
mostra a pasta de trabalho para você inspecionar. Os mesmos comandos existem como `psrunner validate` e `psrunner run`.

## Versões

O `psrunner` tem versão própria no PyPI. Ele segue o protocolo 1, o formato trocado com o runner .NET, enquanto esse
protocolo estiver em uso. Veja a [tabela de compatibilidade](../../README.md#compatibility).
