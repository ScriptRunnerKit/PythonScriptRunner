using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using PythonScriptRunner.Core.Values;

namespace PythonScriptRunner.Core.Execution;

/// <summary>
/// Runs one attempt as a separate <c>python -I -X utf8 -m psrunner.host</c> process with a clean environment,
/// a timeout, cancellation, a memory limit and captured output (protocol version 1).
/// </summary>
internal sealed class ProcessPythonExecutor(TimeProvider time) : IPythonExecutor
{
    private const int ProtocolVersion = 1;
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PumpWait = TimeSpan.FromSeconds(5);

    public async Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        var work = request.WorkDirectory;
        foreach (var folder in (string[])["in", "out", "tmp"])
        {
            Directory.CreateDirectory(Path.Combine(work, folder));
        }

        var inputs = CopyInputFiles(request);
        await File.WriteAllTextAsync(Path.Combine(work, "input.json"), BuildInputDocument(request, inputs).ToJsonString(), cancellationToken)
            .ConfigureAwait(false);
        var scriptPath = await ResolveScriptAsync(request, cancellationToken).ConfigureAwait(false);

        var masker = new SecretMasker(request.Secrets.Values);
        var stdout = new TailBuffer(request.MaxLogBytes);
        var stderr = new TailBuffer(request.MaxLogBytes);
        using var process = new Process { StartInfo = BuildStartInfo(request, scriptPath) };
        var job = OperatingSystem.IsWindows() ? new WindowsJobObject(request.MemoryLimitMb * 1024L * 1024L) : null;
        try
        {
            var started = time.GetTimestamp();
            try
            {
                process.Start();
            }
            catch (Win32Exception ex)
            {
                return ExecutionResult.Failure(ErrorKind.ProcessCrashed, $"Could not start Python ({request.Environment.PythonExecutable}): {ex.Message}", "START_FAILED");
            }

            if (job is not null && OperatingSystem.IsWindows())
            {
                job.Assign(process.Handle);
            }

            process.StandardInput.Close();
            var pumps = Task.WhenAll(
                PumpAsync(stdout, process.StandardOutput.BaseStream),
                PumpAsync(stderr, process.StandardError.BaseStream));

            AttemptStatus? stoppedAs = null;
            using (var timeout = new CancellationTokenSource(request.Timeout, time))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                try
                {
                    await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    stoppedAs = cancellationToken.IsCancellationRequested ? AttemptStatus.Cancelled : AttemptStatus.TimedOut;
                    await KillAsync(process, job).ConfigureAwait(false);
                }
            }

            var processDuration = time.GetElapsedTime(started);
            if (job is not null && OperatingSystem.IsWindows())
            {
                job.Dispose();
                job = null;
            }

            try
            {
                await pumps.WaitAsync(PumpWait, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A grandchild still holds the pipes open; keep what was captured so far.
            }

            var stdoutTail = stdout.ToLogTail(masker);
            var stderrTail = stderr.ToLogTail(masker);
            var exitCode = process.HasExited ? process.ExitCode : -1;
            return stoppedAs switch
            {
                AttemptStatus.Cancelled => Stopped(ErrorKind.Cancelled, "The run was cancelled.", AttemptStatus.Cancelled),
                AttemptStatus.TimedOut => Stopped(ErrorKind.Timeout, $"The attempt exceeded its timeout of {request.Timeout}.", AttemptStatus.TimedOut),
                _ => OutputReader.Read(request, exitCode, processDuration, stdoutTail, stderrTail),
            };

            ExecutionResult Stopped(ErrorKind kind, string message, AttemptStatus status) =>
                ExecutionResult.Failure(kind, message, status: status) with
                {
                    ExitCode = exitCode,
                    ProcessDuration = processDuration,
                    Stdout = stdoutTail,
                    Stderr = stderrTail,
                };
        }
        finally
        {
            if (job is not null && OperatingSystem.IsWindows())
            {
                job.Dispose();
            }
        }
    }

    private static async Task PumpAsync(TailBuffer buffer, Stream source)
    {
        try
        {
            await buffer.PumpAsync(source, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The process was killed or the stream was closed: keep what was captured.
        }
    }

    private static async Task KillAsync(Process process, WindowsJobObject? job)
    {
        if (job is not null && OperatingSystem.IsWindows())
        {
            job.Terminate();
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already exited.
        }

        using var wait = new CancellationTokenSource(KillWait);
        try
        {
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Give up waiting; the attempt is reported as stopped anyway.
        }
    }

    /// <summary>Copies input files to <c>in/&lt;field&gt;/&lt;index&gt;/&lt;name&gt;</c> and returns the inputs with full file references.</summary>
    private static JsonObject CopyInputFiles(ExecutionRequest request)
    {
        var inputs = (JsonObject)request.Inputs.DeepClone();
        foreach (var group in request.Files.GroupBy(f => f.Field))
        {
            var field = request.Stage.Inputs.First(f => f.Name == group.Key);
            var references = new JsonArray();
            foreach (var file in group.OrderBy(f => f.Index))
            {
                var relative = $"in/{file.Field}/{file.Index.ToString(CultureInfo.InvariantCulture)}/{file.FileName}";
                var target = Path.Combine(request.WorkDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file.SourcePath, target, overwrite: true);
                references.Add(new JsonObject
                {
                    [WireValidator.FileMarker] = new JsonObject
                    {
                        ["path"] = relative,
                        ["name"] = file.FileName,
                        ["contentType"] = file.ContentType,
                        ["size"] = file.Size,
                        ["sha256"] = file.Sha256,
                    },
                });
            }

            inputs[group.Key] = field.Type.Kind == FieldKind.List ? references : references[0]!.DeepClone();
        }

        return inputs;
    }

    private static JsonObject BuildInputDocument(ExecutionRequest request, JsonObject inputs) => new()
    {
        ["protocol"] = ProtocolVersion,
        ["context"] = new JsonObject
        {
            ["runId"] = request.RunId.ToString(),
            ["stepKey"] = request.StepKey,
            ["attempt"] = request.Attempt,
            ["stage"] = new JsonObject { ["key"] = request.Stage.Key, ["version"] = request.Stage.Version },
            ["isTest"] = request.IsTest,
            ["inputFields"] = FieldDefinitionJson.ToJson(request.Stage.Inputs),
            ["outputFields"] = FieldDefinitionJson.ToJson(request.Stage.Outputs),
        },
        ["inputs"] = inputs,
    };

    private static async Task<string> ResolveScriptAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        switch (request.Stage.Script)
        {
            case FileScriptSource file:
                return file.Path;
            case InlineScriptSource inline:
                var path = Path.Combine(request.WorkDirectory, "script.py");
                await File.WriteAllTextAsync(path, inline.Content, cancellationToken).ConfigureAwait(false);
                return path;
            default:
                throw new NotSupportedException($"Unsupported script source {request.Stage.Script.GetType().Name}.");
        }
    }

    private static ProcessStartInfo BuildStartInfo(ExecutionRequest request, string scriptPath)
    {
        var work = request.WorkDirectory;
        var temp = Path.Combine(work, "tmp");
        var startInfo = new ProcessStartInfo(request.Environment.PythonExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = work,
        };
        foreach (var argument in (string[])["-I", "-X", "utf8", "-m", "psrunner.host"])
        {
            startInfo.ArgumentList.Add(argument);
        }

        var environment = startInfo.Environment;
        environment.Clear();
        if (OperatingSystem.IsWindows())
        {
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            environment["SystemRoot"] = systemRoot;
            environment["PATH"] = $"{Path.Combine(systemRoot, "System32")};{systemRoot}";
        }
        else
        {
            environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
            environment["HOME"] = temp;
            environment["TMPDIR"] = temp;
        }

        environment["TEMP"] = temp;
        environment["TMP"] = temp;
        environment["PSR_PROTOCOL"] = ProtocolVersion.ToString(CultureInfo.InvariantCulture);
        environment["PSR_INPUT"] = Path.Combine(work, "input.json");
        environment["PSR_OUTPUT"] = Path.Combine(work, "output.json");
        environment["PSR_WORK_DIR"] = work;
        environment["PSR_SCRIPT"] = scriptPath;
        environment["PSR_MEMORY_LIMIT_MB"] = request.MemoryLimitMb.ToString(CultureInfo.InvariantCulture);
        foreach (var (name, value) in request.Secrets)
        {
            environment[$"PSR_SECRET_{name}"] = value;
        }

        return startInfo;
    }
}
