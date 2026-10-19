using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PythonScriptRunner.Core.Diagnostics;

namespace PythonScriptRunner.Core.Environments;

/// <summary>Creates one venv per (Python version, locked requirements, psrunner) with uv and caches it on disk.</summary>
internal sealed class UvEnvironmentManager(
    IOptions<PythonScriptRunnerOptions> options,
    PsrMetrics metrics,
    TimeProvider time,
    ILogger<UvEnvironmentManager> logger) : IEnvironmentManager
{
    private const string ReadyMarker = ".psr-ready";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(20);

    /// <summary>Compiles the script given as argument and prints the outcome as JSON (same check as <c>py_compile</c>).</summary>
    private const string SyntaxChecker = """
        import json
        import sys

        with open(sys.argv[1], encoding="utf-8") as source_file:
            source = source_file.read()
        try:
            compile(source, "script.py", "exec")
        except (SyntaxError, ValueError) as error:
            print(json.dumps({
                "ok": False,
                "message": getattr(error, "msg", None) or str(error),
                "line": getattr(error, "lineno", None),
                "column": getattr(error, "offset", None),
            }))
        else:
            print(json.dumps({"ok": True}))
        """;

    public async Task<ScriptSyntaxError?> CheckSyntaxAsync(string? pythonVersion, string script, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        var folder = Path.Combine(Path.GetTempPath(), "psr-syntax", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var checker = Path.Combine(folder, "check_syntax.py");
            var target = Path.Combine(folder, "script.py");
            await File.WriteAllTextAsync(checker, SyntaxChecker, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(target, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
            var version = pythonVersion ?? options.Value.DefaultPythonVersion;
            var output = await CreateCli().RunAsync(
                ["run", "--no-project", "--python", version, "--directory", folder, "python", "-I", checker, target], null, cancellationToken)
                .ConfigureAwait(false);
            var result = JsonNode.Parse(output.Trim().Split('\n')[^1])!.AsObject();
            return result["ok"]!.GetValue<bool>()
                ? null
                : new ScriptSyntaxError(result["message"]?.GetValue<string>() ?? "invalid syntax", result["line"]?.GetValue<int?>(), result["column"]?.GetValue<int?>());
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    public string ComputeHash(string? pythonVersion, string? lockedRequirements) =>
        Sha256Hex($"{pythonVersion ?? options.Value.DefaultPythonVersion}\n{Normalize(lockedRequirements ?? string.Empty)}\n{PsrunnerIdentity(options.Value.PsrunnerRequirement)}");

    public async Task<string> LockAsync(string? pythonVersion, string? requirements, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requirements))
        {
            return string.Empty;
        }

        var version = pythonVersion ?? options.Value.DefaultPythonVersion;
        List<string> arguments =
        [
            "pip", "compile", "-", "--universal", "--generate-hashes", "--python-version", version,
            "--no-header", "--no-annotate", "--quiet", .. IndexArguments(),
        ];
        return await CreateCli().RunAsync(arguments, requirements, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PythonEnvironment> EnsureAsync(StageDefinition stage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        var settings = options.Value;
        var version = stage.PythonVersion ?? settings.DefaultPythonVersion;
        var locked = stage.LockedRequirements ?? await GetCachedLockAsync(stage, version, cancellationToken).ConfigureAwait(false);
        var hash = ComputeHash(version, locked);
        var directory = Path.Combine(settings.EnvironmentCacheRoot, "envs", hash[..32]);
        var python = OperatingSystem.IsWindows()
            ? Path.Combine(directory, "Scripts", "python.exe")
            : Path.Combine(directory, "bin", "python");
        var marker = Path.Combine(directory, ReadyMarker);
        if (File.Exists(marker))
        {
            return new PythonEnvironment(hash, directory, python, Created: false);
        }

        using var activity = PsrDiagnostics.Source.StartActivity("psr.environment.ensure");
        activity?.SetTag("psr.environment.hash", hash);
        using (await CrossProcessLock.AcquireAsync(directory + ".lock", LockTimeout, cancellationToken).ConfigureAwait(false))
        {
            if (File.Exists(marker))
            {
                activity?.SetTag("psr.environment.created", false);
                return new PythonEnvironment(hash, directory, python, Created: false);
            }

            Log.CreatingEnvironment(logger, hash, version, stage.Key);
            var started = Stopwatch.GetTimestamp();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            var uv = CreateCli();
            await uv.RunAsync(["venv", directory, "--python", version, "--quiet"], null, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(locked))
            {
                var lockFile = Path.Combine(directory, "requirements.lock");
                await File.WriteAllTextAsync(lockFile, locked, cancellationToken).ConfigureAwait(false);
                await uv.RunAsync(
                    ["pip", "sync", lockFile, "--python", python, "--require-hashes", "--quiet", .. IndexArguments()],
                    null,
                    cancellationToken).ConfigureAwait(false);
            }

            await uv.RunAsync(
                ["pip", "install", "--python", python, "--no-deps", "--quiet", settings.PsrunnerRequirement, .. IndexArguments()],
                null,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(marker, time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), cancellationToken)
                .ConfigureAwait(false);

            metrics.EnvironmentCreated(version);
            activity?.SetTag("psr.environment.created", true);
            Log.EnvironmentCreated(logger, hash, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new PythonEnvironment(hash, directory, python, Created: true);
        }
    }

    private async Task<string> GetCachedLockAsync(StageDefinition stage, string version, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stage.Requirements))
        {
            return string.Empty;
        }

        var key = Sha256Hex($"{version}\n{Normalize(stage.Requirements)}");
        var path = Path.Combine(options.Value.EnvironmentCacheRoot, "locks", $"{key[..32]}.txt");
        if (File.Exists(path))
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }

        Log.GeneratingLock(logger, stage.Key, stage.Version);
        var locked = await LockAsync(version, stage.Requirements, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, locked, cancellationToken).ConfigureAwait(false);
        try
        {
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(path))
        {
            // Another process stored the same lock at the same moment (Windows denies replacing a file in use); keep theirs.
            File.Delete(temporary);
        }

        return locked;
    }

    private UvCli CreateCli()
    {
        var uvPath = options.Value.UvPath;
        return new UvCli(ExecutableLocator.Find(uvPath)
            ?? throw new PythonEnvironmentException($"The uv executable '{uvPath}' was not found. Install uv or set UvPath."));
    }

    private IEnumerable<string> IndexArguments()
    {
        var settings = options.Value;
        if (!string.IsNullOrWhiteSpace(settings.PackageIndexUrl))
        {
            yield return "--index-url";
            yield return settings.PackageIndexUrl;
        }

        foreach (var url in settings.ExtraIndexUrls)
        {
            yield return "--extra-index-url";
            yield return url;
        }
    }

    private static string PsrunnerIdentity(string requirement) =>
        File.Exists(requirement) ? "file:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(requirement))) : requirement;

    private static string Normalize(string text) => text.ReplaceLineEndings("\n").Trim();

    private static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
