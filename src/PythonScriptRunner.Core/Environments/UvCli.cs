using System.ComponentModel;
using System.Diagnostics;

namespace PythonScriptRunner.Core.Environments;

/// <summary>Runs the <c>uv</c> command line.</summary>
internal sealed class UvCli(string uvExecutable)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    public async Task<string> RunAsync(IReadOnlyList<string> arguments, string? standardInput, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(uvExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Remove("VIRTUAL_ENV");
        startInfo.Environment.Remove("UV_PROJECT_ENVIRONMENT");
        startInfo.Environment["NO_COLOR"] = "1";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new PythonEnvironmentException($"Could not start uv ('{uvExecutable}'): {ex.Message}", ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            cancellationToken.ThrowIfCancellationRequested();
            throw new PythonEnvironmentException($"uv {Describe(arguments)} did not finish within {Timeout.TotalMinutes} minutes.");
        }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        return process.ExitCode == 0
            ? output
            : throw new PythonEnvironmentException($"uv {Describe(arguments)} failed (exit code {process.ExitCode}): {error.Trim()}");
    }

    private static string Describe(IReadOnlyList<string> arguments) => string.Join(' ', arguments.Take(2));
}
