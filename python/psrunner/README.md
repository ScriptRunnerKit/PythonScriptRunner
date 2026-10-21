# psrunner

Python SDK for [PythonScriptRunner](https://github.com/ScriptRunnerKit/PythonScriptRunner) stages.

A stage is one Python script with typed inputs and outputs. The .NET runner validates the inputs,
runs the script in an isolated process and validates the outputs. `psrunner` handles the protocol,
so the script only contains the logic:

```python
from psrunner import stage

@stage
def run(inputs, ctx):
    a1, ratio, n = inputs["a1"], inputs["ratio"], inputs["n"]
    terms = [a1 * ratio**i for i in range(n)]
    ctx.log.info("generated %d terms", n)
    return {"terms": terms}
```

Test it locally with pytest, without .NET:

```python
from psrunner.testing import run_stage

def test_terms():
    result = run_stage("stages/terms/script.py", {"a1": 1, "ratio": 2, "n": 4},
                       stage_json="stages/terms/stage.json")
    assert result.outputs == {"terms": [1, 2, 4, 8]}
```

Install the `pandas` extra to receive `table` inputs as `pandas.DataFrame`: `pip install psrunner[pandas]`.
