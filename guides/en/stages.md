# Stages

English | [Português](../pt-br/stages.md)

A stage is one Python script and its contract. This page covers the contract, every option of `stage.json`, defining
stages in C#, and reading results in .NET. For the script side, see the [Python SDK](python-sdk.md).

## Folder layout

With the folder catalog (`AddFileCatalog("psr")`), each stage is a folder under `stages/`:

```text
psr/
  stages/
    average/
      stage.json         the contract and settings
      script.py          the code (one function marked with @stage)
      requirements.txt   optional: Python packages, one per line
  pipelines/
    report.pipeline.json
```

When persistence is on, the catalog lives in PostgreSQL instead, and the same folder can be imported as a zip (see
[Persistence](persistence.md)).

## stage.json

```json
{
  "$schema": "https://raw.githubusercontent.com/ScriptRunnerKit/PythonScriptRunner/main/schemas/stage.v1.json",
  "key": "polynomial",
  "version": 1,
  "name": "Evaluate a polynomial",
  "description": "a + b·x + c·x² + … for each x",
  "python": "3.12",
  "timeoutSeconds": 120,
  "memoryLimitMb": 2048,
  "retry": { "maxAttempts": 2, "initialDelaySeconds": 10, "backoffMultiplier": 2 },
  "idempotent": true,
  "secrets": ["SMTP_PASSWORD"],
  "inputs": [
    { "name": "coefficients", "type": "list", "itemType": "number" },
    { "name": "x", "type": "list", "itemType": "number" }
  ],
  "outputs": [
    { "name": "values", "type": "list", "itemType": "number" }
  ]
}
```

The `$schema` line gives you completion and validation in editors such as VS Code.

| Property | Required | Meaning |
|----------|----------|---------|
| `key` | yes | Unique slug: lowercase letters and digits, words separated by single hyphens (`monthly-report`). Used to run the stage. |
| `version` | yes | Integer version. Pipelines reference `key@version`. |
| `name` | yes | Display name. |
| `description` | no | Free text shown in the dashboard. |
| `python` | no | Python version (3.10 or newer). Default: `DefaultPythonVersion`, which is 3.12. |
| `script` | no | Script file, relative to the folder. Default: `script.py`. |
| `requirements` | no | Requirements file, relative to the folder. Default: `requirements.txt`, if it exists. |
| `timeoutSeconds` | no | Time limit of one attempt. Default: `DefaultTimeout`, which is 5 minutes. |
| `memoryLimitMb` | no | Memory limit of one attempt. Default: `DefaultMemoryLimitMb`, which is 1024. |
| `retry` | no | Automatic retry after a failed attempt. See [Retry](#retry). |
| `idempotent` | no | `true` when running the stage twice has the same effect as once. Lets a worker resume it after another worker was lost. Default: `false`. |
| `secrets` | no | Names of the secrets the script receives (uppercase, digits and `_`). See [Secrets](#secrets). |
| `inputs`, `outputs` | yes | The fields of the contract. Either list can be empty. |

## Field types

Each input and output has a `name` and a `type`. The library converts values on both sides:

| Type | In .NET (inputs and `GetOutput<T>`) | In Python (`inputs[...]` and return value) |
|------|-------------------------------------|-------------------------------------------|
| `number` | `double` | `float` (`NaN` and infinity are rejected) |
| `integer` | `int`, `long` | `int` |
| `decimal` | `decimal` | `decimal.Decimal` (exact, sent as text) |
| `string` | `string` | `str` |
| `bool` | `bool` | `bool` |
| `date` | `DateOnly` | `datetime.date` |
| `datetime` | `DateTimeOffset` | `datetime.datetime` with a UTC offset (values without one are rejected) |
| `enum` | `string` from `enumValues` | `str` from `enumValues` |
| `json` | any JSON value (`JsonNode`, or your own type) | `dict`, `list`, `str`, number, `bool` or `None` |
| `table` | a list of records (your own record type, or `JsonArray`) | `list[dict]`, or `pandas.DataFrame` with the `pandas` extra |
| `file` | `PsrFile` | `psrunner.File` (input); `ctx.new_file(...)` (output) |
| `list` | arrays and lists of the item type | `list` of the item type |

Field properties:

| Property | Meaning |
|----------|---------|
| `name` | Field name: lowercase letters, digits and `_`, starting with a letter or `_`. |
| `type` | One of the types above. |
| `itemType` | Item type of a `list` (any type except `list`). |
| `required` | Default `true`. An optional input may be omitted; an optional output may be missing. |
| `default` | Inputs only: the value used when the input is not given. |
| `description` | Shown in the dashboard and in the generated run form. |
| `enumValues` | Allowed values of an `enum` (or of a `list` of `enum`). |
| `contentTypes` | Allowed MIME types of a `file`, for example `["application/pdf"]`. |
| `columns` | Optional schema of a `table`: `[{ "name": "x", "type": "number" }]`. Each column has a scalar type. |

## Python packages

Put a `requirements.txt` next to the script, with one package per line (`numpy`, `pandas>=2.2`). The runner creates one
environment per Python version and set of requirements with uv, caches it, and reuses it for every stage with the same
set. With persistence, publishing a stage locks the requirements with hashes (`uv pip compile --generate-hashes`), so a
published version always runs with the same package versions.

The `psrunner` SDK is installed in every environment automatically. Do not list it in `requirements.txt`.

## Defining a stage in C#

The same contract can be declared in code, without files:

```csharp
builder.Services.AddPythonScriptRunner()
    .AddStage("average", s => s
        .Script("""
            from psrunner import stage

            @stage
            def run(inputs, ctx):
                v = inputs["values"]
                return {"average": sum(v) / len(v)}
            """)
        .Python("3.12")
        .Requirements("numpy")
        .Input("values", FieldType.ListOf(FieldType.Number))
        .Output("average", FieldType.Number)
        .Timeout(TimeSpan.FromMinutes(1))
        .MemoryLimitMb(512));
```

`FieldType` has `Number`, `Integer`, `Decimal`, `String`, `Bool`, `Date`, `DateTime`, `Json`, `Table`, `File`,
`Enum("a", "b")` and `ListOf(itemType)`. An invalid definition fails at startup with `PsrConfigurationException`.

## Running a stage and reading the result

```csharp
await using var result = await runner.RunStageAsync("average", new { values = new[] { 2.0, 4.0, 9.0 } });

if (result.Status == RunStatus.Succeeded)
{
    double average = result.GetOutput<double>("average");
}
else
{
    Console.WriteLine($"{result.Error!.Kind}: {result.Error.Message}");
}
```

- **Inputs:** an anonymous object, a dictionary or any object that serializes to the expected JSON. Files are passed as
  `PsrFile`: `PsrFile.FromPath(...)`, `FromStream`, `FromBytes` or `FromText`.
- **Before the run:** inputs that do not match the contract throw `InputValidationException`, and no run is created.
- **After the run:** failures are reported in `result.Status` and `result.Error`. `result.EnsureSuccess()` throws if the
  run did not succeed.
- **Reading outputs:**
  - `GetOutput<T>(name)` reads a value;
  - `GetFile(name)` and `GetFiles(name)` return `PsrFile` objects (`OpenReadAsync`, `SaveAsAsync`).
- **Disposing:** `RunResult` is `IAsyncDisposable`. Disposing it deletes the local output files, so save the ones you
  need first (`SaveAsAsync`).
- **Options:** `RunOptions` picks a specific `Version`, sets an `IdempotencyKey` (with persistence) and records who
  started the run.

Error kinds you can get in `result.Error.Kind`:

| Kind | Meaning |
|------|---------|
| `InputValidation` | An input did not match the contract. |
| `OutputContractViolation` | The script returned something outside the contract (missing output, wrong type, `NaN`). |
| `ScriptError` | The script raised an exception, or a `StageError` with a `code`. |
| `Timeout` | The attempt exceeded its timeout. |
| `MemoryLimitExceeded` | The attempt exceeded its memory limit. |
| `Cancelled` | The run was cancelled. |
| `WorkerLost` | The worker running the step stopped answering (queued runs). |
| `EnvironmentSetupFailed` | uv could not create the environment (package not found, no network, unknown Python version). |
| `ArtifactLimitExceeded` | Files exceeded `MaxFileBytes` or `MaxRunFilesBytes`. |
| `SecretNotFound` | A declared secret is not configured. Python does not even start. |
| `ProcessCrashed` | The Python process could not start or ended without a result. |
| `Expired` | The run reached the retention limit before finishing. |

## Retry

`retry` makes the runner try again after a failed attempt:

- **`maxAttempts`:** how many extra attempts are allowed (0 to 20). The default, 0, means no automatic retry.
- **Delay before each retry:** `initialDelaySeconds × backoffMultiplier^(n-1)`, capped at 10 minutes. The defaults are 10
  seconds and 2.

A cancelled run is never retried. Queued runs wait for the delay without holding a worker.

## Secrets

A stage declares the names of the secrets it needs. The values come from configuration, in the section
`PythonScriptRunner:Secrets` (for example the environment variable `PythonScriptRunner__Secrets__SMTP_PASSWORD`), or from
your own `ISecretProvider`. Values are never written to inputs, logs, metrics or the database. The process of that stage
is the only one that receives them, and they are masked (`***`) in the captured output. In Python, read them with
`ctx.secrets["SMTP_PASSWORD"]`.
