using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PythonScriptRunner.Core.Values;

namespace PythonScriptRunner.Core.Execution;

/// <summary>Reads <c>output.json</c> and validates outputs and output files against the stage contract.</summary>
internal static class OutputReader
{
    private const int WindowsStatusNoMemory = unchecked((int)0xC0000017);

    public static ExecutionResult Read(ExecutionRequest request, int exitCode, TimeSpan processDuration, LogTail stdout, LogTail stderr)
    {
        var result = ReadDocument(request, exitCode);
        return result with { ExitCode = exitCode, ProcessDuration = processDuration, Stdout = stdout, Stderr = stderr };
    }

    private static ExecutionResult ReadDocument(ExecutionRequest request, int exitCode)
    {
        var document = TryLoad(Path.Combine(request.WorkDirectory, "output.json"));
        if (document is null || !IsProtocolOne(document["protocol"]))
        {
            var kind = OperatingSystem.IsWindows() && exitCode == WindowsStatusNoMemory
                ? ErrorKind.MemoryLimitExceeded
                : ErrorKind.ProcessCrashed;
            return ExecutionResult.Failure(kind, $"The Python process exited with code {exitCode} without a valid output.json (see stderr).", "NO_OUTPUT");
        }

        WireValidator.TryGetString(document["status"], out var status);
        if (status == "error" && document["error"] is JsonObject error)
        {
            WireValidator.TryGetString(error["kind"], out var kindText);
            WireValidator.TryGetString(error["message"], out var message);
            WireValidator.TryGetString(error["code"], out var code);
            WireValidator.TryGetString(error["traceback"], out var traceback);
            var kind = Enum.TryParse<ErrorKind>(kindText, ignoreCase: false, out var parsed) ? parsed : ErrorKind.ScriptError;
            return new ExecutionResult
            {
                Status = AttemptStatus.Failed,
                Error = new RunError(kind, message.Length > 0 ? message : "The script failed.")
                {
                    Code = code.Length > 0 ? code : null,
                    Traceback = traceback.Length > 0 ? traceback : null,
                    Details = error["details"]?.DeepClone(),
                },
            };
        }

        if (status != "ok")
        {
            return ExecutionResult.Failure(ErrorKind.ProcessCrashed, $"output.json has an unknown status '{status}'.", "INVALID_OUTPUT");
        }

        return ValidateOutputs(request, document["outputs"] as JsonObject ?? []);
    }

    private static ExecutionResult ValidateOutputs(ExecutionRequest request, JsonObject outputs)
    {
        var errors = new List<WireError>();
        var declared = request.Stage.Outputs.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        errors.AddRange(outputs
            .Where(p => !declared.Contains(p.Key))
            .Select(p => new WireError(p.Key, "UNDECLARED_OUTPUT", "output is not declared by the stage")));

        var wire = new JsonObject();
        var files = new List<OutputFile>();
        var budget = request.FileBudgetBytes;
        foreach (var field in request.Stage.Outputs)
        {
            outputs.TryGetPropertyValue(field.Name, out var value);
            if (value is null)
            {
                if (field.Required)
                {
                    errors.Add(new(field.Name, "MISSING_REQUIRED_OUTPUT", "required output is missing"));
                }

                continue;
            }

            var before = errors.Count;
            WireValidator.Validate(value, field, field.Name, errors);
            if (errors.Count > before)
            {
                continue;
            }

            if (!field.Type.IsFile)
            {
                wire[field.Name] = value.DeepClone();
                continue;
            }

            var references = field.Type.Kind == FieldKind.List ? [.. value.AsArray()] : new List<JsonNode?> { value };
            var metadata = new JsonArray();
            for (var i = 0; i < references.Count; i++)
            {
                var path = field.Type.Kind == FieldKind.List ? $"{field.Name}[{i}]" : field.Name;
                var file = ResolveFile(request, references[i]!.AsObject(), field.Name, i, path, ref budget, errors);
                if (file is not null)
                {
                    files.Add(file);
                    metadata.Add(new JsonObject
                    {
                        [WireValidator.FileMarker] = new JsonObject
                        {
                            ["name"] = file.FileName,
                            ["contentType"] = file.ContentType,
                            ["size"] = file.Size,
                            ["sha256"] = file.Sha256,
                        },
                    });
                }
            }

            wire[field.Name] = field.Type.Kind == FieldKind.List ? metadata : metadata.FirstOrDefault()?.DeepClone();
        }

        if (errors.Count == 0)
        {
            return new ExecutionResult { Status = AttemptStatus.Succeeded, Outputs = wire, Files = files };
        }

        var limit = errors.Any(e => e.Code is "FILE_TOO_LARGE" or "RUN_FILES_TOO_LARGE");
        return ExecutionResult.Failure(
            limit ? ErrorKind.ArtifactLimitExceeded : ErrorKind.OutputContractViolation,
            string.Join("; ", errors.Select(e => $"'{e.Path}': {e.Message}")),
            errors[0].Code);
    }

    private static OutputFile? ResolveFile(
        ExecutionRequest request, JsonObject reference, string field, int index, string path, ref long budget, List<WireError> errors)
    {
        var info = reference[WireValidator.FileMarker]!.AsObject();
        if (!WireValidator.TryGetString(info["path"], out var relative))
        {
            errors.Add(new(path, "OUTPUT_FILE_MISSING", "file reference has no path"));
            return null;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var outDirectory = Path.GetFullPath(Path.Combine(request.WorkDirectory, "out")) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(request.WorkDirectory, relative));
        if (!fullPath.StartsWith(outDirectory, comparison))
        {
            errors.Add(new(path, "OUTPUT_FILE_OUTSIDE_OUT", "output files must be created inside out/"));
            return null;
        }

        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            errors.Add(new(path, "OUTPUT_FILE_MISSING", $"file was not written: {relative}"));
            return null;
        }

        if (file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            errors.Add(new(path, "OUTPUT_FILE_OUTSIDE_OUT", "symbolic links are not allowed"));
            return null;
        }

        if (file.Length > request.MaxFileBytes)
        {
            errors.Add(new(path, "FILE_TOO_LARGE", $"file has {file.Length} bytes; the limit is {request.MaxFileBytes}"));
            return null;
        }

        budget -= file.Length;
        if (budget < 0)
        {
            errors.Add(new(path, "RUN_FILES_TOO_LARGE", "the files of this run exceed the run limit"));
            return null;
        }

        string sha256;
        using (var stream = file.OpenRead())
        {
            sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        var name = WireValidator.TryGetString(info["name"], out var given) && given.Length > 0 ? Path.GetFileName(given) : file.Name;
        var contentType = WireValidator.TryGetString(info["contentType"], out var type) && type.Length > 0 ? type : PsrFile.GuessContentType(name);
        return new OutputFile(field, index, fullPath, name, contentType, file.Length, sha256);
    }

    private static JsonObject? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsProtocolOne(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var protocol) && protocol == 1;
}
