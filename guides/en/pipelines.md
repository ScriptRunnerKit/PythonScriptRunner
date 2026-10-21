# Pipelines

English | [Português](../pt-br/pipelines.md)

A pipeline runs stages one after the other. Each step can use the pipeline's inputs, the outputs of any earlier step, or
fixed values, and the pipeline declares its own outputs. Types and bindings are validated when the catalog loads (with
persistence, when a version is published), so a pipeline that cannot run is rejected before it runs.

## The pipeline file

With the folder catalog, pipelines are files named `pipelines/<name>.pipeline.json`:

```json
{
  "$schema": "https://raw.githubusercontent.com/ScriptRunnerKit/PythonScriptRunner/main/schemas/pipeline.v1.json",
  "key": "progression-calc",
  "version": 1,
  "name": "Progression calculation",
  "inputs": [
    { "name": "a1", "type": "number" },
    { "name": "ratio", "type": "number" },
    { "name": "n", "type": "integer" },
    { "name": "coefficients", "type": "list", "itemType": "number" }
  ],
  "steps": [
    { "key": "terms", "stage": "progression-terms@1" },
    { "key": "polynomial", "stage": "polynomial@1",
      "bindings": { "coefficients": "$pipeline.coefficients", "x": "$steps.terms.terms" } },
    { "key": "statistics", "stage": "statistics@1",
      "bindings": { "values": "$steps.polynomial.values", "precision": { "const": 4 } } }
  ],
  "outputs": [
    { "name": "sum", "source": "$steps.statistics.sum" },
    { "name": "terms", "source": "$steps.terms.terms", "description": "Terms of the progression" }
  ]
}
```

| Property | Meaning |
|----------|---------|
| `key`, `version`, `name`, `description` | Same rules as a stage. |
| `inputs` | Fields of the pipeline, with the same types and properties as stage fields. |
| `steps` | The steps, in execution order. Each has a `key` (slug), a `stage` (`stage-key@version`), an optional `displayName` and optional `bindings`. |
| `outputs` | What the pipeline returns. Each output has a `name` and a `source` (`$steps.<step>.<field>`); its type comes from that step output. |

## Bindings

Each input of a step gets its value from one of three sources:

| Binding | Example | Value |
|---------|---------|-------|
| Pipeline input | `"$pipeline.coefficients"` | The pipeline input with that name. |
| Earlier step | `"$steps.terms.terms"` | The output `terms` of the step `terms`. Any earlier step can be used, not only the previous one. |
| Constant | `{ "const": 4 }` | A fixed value. |

An input without an explicit binding uses the **implicit binding by name**:
- in the first step, the pipeline input with the same name;
- in the other steps, the output with the same name of the previous step.

When the types are compatible but not the same (an `integer` feeding a `decimal`, for example), the value is converted.
An incompatible binding is a validation error.

## Running a pipeline

```csharp
await using var result = await runner.RunPipelineAsync("progression-calc",
    new { a1 = 1, ratio = 2, n = 4, coefficients = new[] { 1.0, 0.0, 1.0 } });

result.EnsureSuccess();
double sum = result.GetOutput<double>("sum");
double[] terms = result.GetStepOutput<double[]>("terms", "terms");   // any step output, useful for debugging
```

`result.Steps` lists every step with its status, attempts, duration and error.

If a step fails, the following steps end as `Skipped` and the run ends as `Failed`, with the error of the step that
failed.
- **Synchronous runs:** you start a new run.
- **With persistence:** `IRunManager.ReprocessAsync` retries only from the step that failed, reusing the outputs of the
  steps that succeeded. See [Persistence](persistence.md).

## Defining a pipeline in C#

```csharp
builder.Services.AddPythonScriptRunner()
    .AddPipeline("progression-calc", p => p
        .Name("Progression calculation")
        .Input("a1", FieldType.Number)
        .Input("ratio", FieldType.Number)
        .Input("n", FieldType.Integer)
        .Input("coefficients", FieldType.ListOf(FieldType.Number))
        .Step("terms", "progression-terms@1")
        .Step("polynomial", "polynomial@1", s => s
            .Bind("coefficients", new PipelineInputSource("coefficients"))
            .Bind("x", new StepOutputSource("terms", "terms")))
        .Step("statistics", "statistics@1", s => s
            .Bind("values", new StepOutputSource("polynomial", "values"))
            .Bind("precision", new ConstantSource(JsonValue.Create(4))))
        .Output("sum", StepOutputSource.Step("statistics", "sum")));
```

## Settings of queued runs

With the queue (see [Queue, worker and scheduling](queue-and-worker.md)), each pipeline also has:
- **Maximum concurrent runs:** the extra runs wait as `Pending`, in order.
- **Retention:** it can only be shorter than the global retention.

Both are changed in the dashboard or with `ICatalogAdmin.UpdatePipelineSettingsAsync` and `UpdatePipelineRetentionAsync`.
