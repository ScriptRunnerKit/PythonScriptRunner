using System.Diagnostics;
using PythonScriptRunner.Core.Environments;

[assembly: AssemblyFixture(typeof(PythonScriptRunner.Core.Tests.Infrastructure.PythonFixture))]

namespace PythonScriptRunner.Core.Tests.Infrastructure;

/// <summary>
/// Builds the psrunner wheel from <c>python/psrunner</c> once and warms the default Python environment,
/// so integration tests run the real protocol end to end.
/// </summary>
public sealed class PythonFixture : IAsyncLifetime
{
    public string RepositoryRoot { get; } = FindRepositoryRoot();

    public string UvPath { get; } = FindUv();

    public string EnvironmentCacheRoot { get; } = Path.Combine(Path.GetTempPath(), "psr-tests", "env-cache");

    public string WheelPath { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var output = Path.Combine(Path.GetTempPath(), "psr-tests", "wheels", Guid.NewGuid().ToString("N"));
        await RunAsync(UvPath, ["build", "--wheel", "--out-dir", output, Path.Combine(RepositoryRoot, "python", "psrunner")]);
        WheelPath = Directory.GetFiles(output, "*.whl").Single();

        await using var host = RunnerHost.Create(this, builder => builder.AddStage("warmup", s => s
            .Script("""
                from psrunner import stage

                @stage
                def run(inputs, ctx):
                    return {"ok": True}
                """)
            .Output("ok", FieldType.Bool)));
        await using var result = await host.Runner.RunStageAsync("warmup", null);
        result.EnsureSuccess();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task RunAsync(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} failed: {await stdout} {await stderr}");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PythonScriptRunner.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (PythonScriptRunner.slnx) not found.");
    }

    private static string FindUv()
    {
        var configured = Environment.GetEnvironmentVariable("PSR_UV_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (ExecutableLocator.Find("uv") is { } found)
        {
            return found;
        }

        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "uv.exe");
        return File.Exists(winget) ? winget : throw new InvalidOperationException("uv was not found. Install it or set PSR_UV_PATH.");
    }
}
