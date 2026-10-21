# psrunner

[![PyPI](https://img.shields.io/pypi/v/psrunner?label=PyPI)](https://pypi.org/project/psrunner/)
[![Python](https://img.shields.io/pypi/pyversions/psrunner)](https://pypi.org/project/psrunner/)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/LICENSE)

The Python SDK of [PythonScriptRunner](https://github.com/ScriptRunnerKit/PythonScriptRunner), a .NET library that runs
Python scripts with a typed contract.

A **stage** is one Python script with typed inputs and outputs. The .NET runner validates the inputs, runs the script in
an isolated process with a timeout and a memory limit, and validates the outputs. `psrunner` handles the protocol between
the two sides, so the script only contains the logic.

**Documentation:** [Python SDK guide](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/en/python-sdk.md) |
[guia em português](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/guides/pt-br/python-sdk.md)

## Installation

The .NET runner installs `psrunner` in every environment it creates, so stages never list it in their requirements.
Install it yourself to write and test scripts locally:

```bash
pip install psrunner             # Python 3.10 or newer
pip install "psrunner[pandas]"   # table inputs arrive as pandas.DataFrame
```

## Writing a stage

```python
from psrunner import stage, StageError

@stage
def run(inputs, ctx):
    a1, ratio, n = inputs["a1"], inputs["ratio"], inputs["n"]
    if n < 1:
        raise StageError("n must be at least 1.", code="INVALID_N")

    terms = [a1 * ratio**i for i in range(n)]
    ctx.log.info("generated %d terms", n)
    return {"terms": terms}
```

The contract lives in `stage.json`, next to the script:

```json
{
  "key": "progression-terms",
  "version": 1,
  "name": "Progression terms",
  "inputs": [
    { "name": "a1", "type": "number" },
    { "name": "ratio", "type": "number" },
    { "name": "n", "type": "integer" }
  ],
  "outputs": [ { "name": "terms", "type": "list", "itemType": "number" } ]
}
```

The rules:
- **One entry point:** exactly one function per script is marked with `@stage`. It receives the inputs, already
  converted, and the context.
- **Return value:** it returns a dict with the outputs. Required outputs must be present, and outputs outside the contract
  are an error.

## Types

| Contract type | Python value |
|---------------|--------------|
| `number` | `float` (`NaN` and infinity are rejected) |
| `integer` | `int` |
| `decimal` | `decimal.Decimal` |
| `string` | `str` |
| `bool` | `bool` |
| `date` | `datetime.date` |
| `datetime` | `datetime.datetime` with a UTC offset |
| `enum` | `str`, one of `enumValues` |
| `json` | `dict`, `list`, `str`, number, `bool` or `None` |
| `table` | `list[dict]`, or `pandas.DataFrame` with the `pandas` extra |
| `file` | `psrunner.File` |
| `list` | `list` of the item type |

## The context

| Member | Description |
|--------|-------------|
| `ctx.log` | A `logging.Logger`; its lines appear in the run's logs. |
| `ctx.secrets` | Read-only mapping with the secrets the stage declares. |
| `ctx.new_file(name, content_type=None)` | Reserves an output file and returns a `File`; write to `file.path` and return it. |
| `ctx.run_id`, `ctx.step_key`, `ctx.attempt` | Identify the run, the pipeline step and the attempt. |
| `ctx.stage_key`, `ctx.stage_version`, `ctx.is_test` | The stage being run, and whether this is a test of a draft. |
| `ctx.work_dir`, `ctx.out_dir`, `ctx.tmp_dir` | The attempt's own folders. |

A `File` has `path`, `name`, `content_type`, `size` and `sha256`, and the methods `read_bytes()`, `read_text()` and
`open()`.

```python
@stage
def run(inputs, ctx):
    report = ctx.new_file("report.csv", "text/csv")
    report.path.write_text("x,y\n1,2\n", encoding="utf-8")
    return {"report": report}
```

## Errors

- **Business errors:** `StageError(message, code="CODE", details={...})` fails the run as `ScriptError` with that code.
- **Unexpected exceptions:** any other exception also becomes `ScriptError`, with the traceback of your script.
- **Contract violations:** a missing output, a wrong type or a non-finite number becomes `OutputContractViolation`.

## Testing with pytest

`psrunner.testing.run_stage` runs the function with the same conversions and validations as the real runner, without
.NET:

```python
from psrunner.testing import run_stage

def test_terms():
    result = run_stage(
        "stages/progression-terms/script.py",
        {"a1": 1, "ratio": 2, "n": 4},
        stage_json="stages/progression-terms/stage.json",
    )
    assert result.ok
    assert result.outputs == {"terms": [1, 2, 4, 8]}
```

`run_stage` also accepts `secrets`, `files` (file fields mapped to local paths), `work_dir` and `run_id`. The result has
`ok`, `outputs`, `wire_outputs`, `error` (`kind`, `code`, `message`, `traceback`), `logs` and `work_dir`.

## Command line

```bash
psrunner validate stages/progression-terms
psrunner run stages/progression-terms --input sample.json --secret NAME=value --keep
```

| Command | What it does |
|---------|--------------|
| `validate <folder>` | Checks `stage.json` and that the script has exactly one `@stage` function. |
| `run <folder>` | Runs the stage with the real protocol, in a separate process and a temporary folder. In `sample.json`, file inputs are written as `{"$path": "relative/path"}`. |

The same commands work as `python -m psrunner`.

## Compatibility

`psrunner` follows protocol 1, the format exchanged with the .NET runner, for as long as that protocol is in use.

| psrunner | Protocol | PythonScriptRunner (NuGet) | Python |
|----------|----------|----------------------------|--------|
| 0.1.x | 1 | 0.1.x | 3.10 to 3.13 |

## License

[MIT](https://github.com/ScriptRunnerKit/PythonScriptRunner/blob/main/LICENSE)
