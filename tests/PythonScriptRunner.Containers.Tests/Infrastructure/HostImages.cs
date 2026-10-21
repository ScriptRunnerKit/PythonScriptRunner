using System.Diagnostics;

namespace PythonScriptRunner.Containers.Tests.Infrastructure;

/// <summary>
/// The host images under test (spec 009): <c>PSR_TEST_WORKER_IMAGE</c> / <c>PSR_TEST_DASHBOARD_IMAGE</c> when the CI already
/// built them, otherwise built here from the Dockerfiles with <c>docker build</c> (BuildKit).
/// </summary>
public static class HostImages
{
    public const string Worker = "worker";

    public const string Dashboard = "dashboard";

    private static readonly string Root = FindRoot();

    public static async Task<string> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        var variable = host == Worker ? "PSR_TEST_WORKER_IMAGE" : "PSR_TEST_DASHBOARD_IMAGE";
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } image)
        {
            return image;
        }

        var tag = $"psr-{host}:test";
        var project = host == Worker ? "PythonScriptRunner.WorkerHost" : "PythonScriptRunner.DashboardHost";
        var (code, output) = await DockerAsync(["build", "-f", Path.Combine("hosts", project, "Dockerfile"), "-t", tag, "."], cancellationToken);
        code.ShouldBe(0, $"docker build of {tag} failed:\n{output}");
        return tag;
    }

    /// <summary>Runs the docker CLI in the repository root and returns the exit code with stdout and stderr.</summary>
    public static async Task<(int Code, string Output)> DockerAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("docker") { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdout + await stderr);
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PythonScriptRunner.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (PythonScriptRunner.slnx) was not found.");
    }
}
