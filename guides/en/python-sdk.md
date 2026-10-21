# Python SDK (psrunner)

English | [Português](../pt-br/python-sdk.md)

`psrunner` is the Python side of PythonScriptRunner. It reads the inputs, converts them to Python types, calls your
function, checks the outputs against the contract and sends them back to .NET. The script only contains the logic.

The runner installs `psrunner` in every environment it creates, so a stage never lists it in `requirements.txt`. Install
it yourself only to write and test scripts locally:

```bash
pip install psrunner             # Python 3.10 or newer
pip install "psrunner[pandas]"   # table inputs arrive as pandas.DataFrame
```

## Writing a stage

```python
from psrunner import stage, StageError

@stage
def run(inputs, ctx):
    values = inputs["values"]
    if not values:
        raise StageError("At least one value is required.", code="EMPTY_INPUT")

    ctx.log.info("averaging %d values", len(values))
    return {"average": sum(values) / len(values)}
```

The rules:
- **One entry point:** exactly one function per script is marked with `@stage`. It receives `inputs` (a dict with the
  inputs, already converted) and `ctx` (the context below).
- **Return value:** it returns a dict with the outputs. Required outputs must be present; outputs not declared in the
  contract are an error.
- **Types:** values are converted as described in [Field types](stages.md#field-types). For example, `decimal` arrives as
  `Decimal`, and `datetime` must carry a UTC offset.

## The context (`ctx`)

| Member | Description |
|--------|-------------|
| `ctx.log` | A `logging.Logger`. Its lines appear in the run's logs (dashboard and `RunResult`). `print` also works and goes to stdout. |
| `ctx.secrets` | Read-only mapping with the secrets the stage declares: `ctx.secrets["SMTP_PASSWORD"]`. |
| `ctx.new_file(name, content_type=None)` | Reserves an output file and returns a `File`; write to `file.path`. |
| `ctx.run_id`, `ctx.step_key`, `ctx.attempt` | Identify the run, the pipeline step and the attempt number. |
| `ctx.stage_key`, `ctx.stage_version` | The stage being run. |
| `ctx.is_test` | `True` for test runs of a draft. |
| `ctx.work_dir`, `ctx.out_dir`, `ctx.tmp_dir` | The attempt's own folders. Use `tmp_dir` for scratch files. |

## Files

A `file` input arrives as a `psrunner.File`:

| Member | Description |
|--------|-------------|
| `path` | Local path of the file. |
| `name`, `content_type`, `size`, `sha256` | Metadata. |
| `read_bytes()`, `read_text(encoding="utf-8")`, `open(mode="rb")` | Read the content. |

To produce a file output, reserve it with `ctx.new_file` and return the `File`:

```python
@stage
def run(inputs, ctx):
    report = ctx.new_file("report.csv", "text/csv")
    report.path.write_text("x,y\n1,2\n", encoding="utf-8")
    return {"report": report}
```

File names must be plain names, without folders. The runner enforces `MaxFileBytes` per file and `MaxRunFilesBytes` per
run (see [Configuration](configuration.md)).

## Errors

- **Business errors:** raise `StageError(message, code="SOME_CODE", details={...})`. The run fails with kind
  `ScriptError` and that `code`, so the .NET side and the dashboard can react to it.
- **Unexpected exceptions:** any other exception also becomes `ScriptError`. The error carries the traceback of your
  script, which the dashboard shows.
- **Contract violations:** a missing output, a wrong type or a non-finite number becomes `OutputContractViolation`.

## Testing with pytest

`psrunner.testing.run_stage` runs the function with the same conversions and validations as the real runner, without
.NET:

```python
from psrunner.testing import run_stage

def test_average():
    result = run_stage(
        "psr/stages/average/script.py",
        {"values": [2, 4, 9]},
        stage_json="psr/stages/average/stage.json",
    )
    assert result.ok
    assert result.outputs == {"average": 5.0}
```

| Parameter | Description |
|-----------|-------------|
| `script` | Path of the script, or the function itself. |
| `inputs` | Dict with the inputs. |
| `stage_json` | Path of the contract. With it, inputs and outputs are validated; without it, inputs are passed as given. |
| `secrets` | Dict with secret values. |
| `files` | Maps file fields to local paths (a list for `list` of `file`). |
| `work_dir`, `run_id` | Optional folder and run id. |

The result has these fields:
- `ok`: `True` when the stage succeeded;
- `outputs`: the Python values;
- `wire_outputs`: the JSON sent to .NET;
- `error`: with `kind`, `code`, `message` and `traceback`;
- `logs`: the log lines;
- `work_dir`: the folder the run used.

## Command line

```bash
python -m psrunner validate psr/stages/average
python -m psrunner run psr/stages/average --input sample.json --secret SMTP_PASSWORD=secret --keep
```

| Command | What it does |
|---------|--------------|
| `validate <folder>` | Checks `stage.json` and that the script has exactly one `@stage` function. |
| `run <folder>` | Runs the stage locally with the real protocol, in a separate process and a temporary folder. |

In the `sample.json` of `run`, a file input is written as `{"$path": "relative/to/sample.json"}`. `--keep` prints the work
folder so you can inspect it. The same commands are available as `psrunner validate` and `psrunner run`.

## Versions

`psrunner` has its own version on PyPI. It follows protocol 1, the format exchanged with the .NET runner, for as long as
that protocol is in use. See the [compatibility table](../../README.md#compatibility).
