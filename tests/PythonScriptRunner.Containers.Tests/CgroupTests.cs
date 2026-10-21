using System.Net.Http.Json;
using PythonScriptRunner.Containers.Tests.Infrastructure;
using PythonScriptRunner.Tests.Infrastructure;

namespace PythonScriptRunner.Containers.Tests;

/// <summary>
/// Spec 001, AC-30 (D-180): with a writable cgroup v2 (a privileged container) the memory limit of an attempt is the
/// memory really used, enforced by the kernel, and the numeric libraries keep their threads.
/// </summary>
public sealed class CgroupTests(HostStack stack)
{
    private static readonly Dictionary<string, string> Stages = new()
    {
        ["stages/consome-memoria/stage.json"] = """
            {"key": "consome-memoria", "version": 1, "name": "Consome memória", "memoryLimitMb": 128,
             "inputs": [], "outputs": [{"name": "tamanho", "type": "integer"}]}
            """,
        ["stages/consome-memoria/script.py"] = """
            from psrunner import stage


            @stage
            def run(inputs, ctx):
                data = bytearray(512 * 1024 * 1024)
                data[::4096] = b"\x01" * (len(data) // 4096)
                return {"tamanho": len(data)}
            """,
        ["stages/threads-numpy/stage.json"] = """
            {"key": "threads-numpy", "version": 1, "name": "Threads do numpy",
             "inputs": [], "outputs": [{"name": "threads", "type": "string"}]}
            """,
        ["stages/threads-numpy/requirements.txt"] = "numpy\n",
        ["stages/threads-numpy/script.py"] = """
            import os

            import numpy
            from psrunner import stage


            @stage
            def run(inputs, ctx):
                numpy.ones(3)
                return {"threads": os.environ.get("OPENBLAS_NUM_THREADS", "")}
            """,
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spec001_AC30_InAPrivilegedContainer_TheCgroupEnforcesTheMemoryLimit_AndNumpyKeepsItsThreads()
    {
        var database = await stack.CreateDatabaseAsync("ac30");
        await using var dashboard = stack.Host(stack.DashboardImage, database, HostStack.DashboardUsers());
        await dashboard.StartAsync(Ct);
        await using var worker = stack.Host(stack.WorkerImage, database, privileged: true);
        await worker.StartAsync(Ct);
        using var http = await PanelApi.LoginAsync(
            new Uri($"http://{dashboard.Hostname}:{dashboard.GetMappedPublicPort(HostStack.HttpPort)}"), "admin", HostStack.Password);
        await PanelApi.ImportAndPublishAsync(http, "ac30", Stages);
        var noInputs = JsonContent.Create(new { inputs = new { } });

        var greedy = await PanelApi.WaitForEndAsync(http, await PanelApi.RunStageAsync(http, "consome-memoria", noInputs), TimeSpan.FromMinutes(8));
        var numpy = await PanelApi.WaitForSuccessAsync(http, await PanelApi.RunStageAsync(http, "threads-numpy", noInputs), TimeSpan.FromMinutes(8));

        var (stdout, _) = await worker.GetLogsAsync(ct: Ct);
        var memoryLimits = stdout.Split('\n').Where(l => l.Contains("Memory limits", StringComparison.Ordinal));
        stdout.ShouldContain("cgroup v2", customMessage: string.Join(Environment.NewLine, memoryLimits));
        greedy["status"]!.GetValue<string>().ShouldBe("Failed");
        greedy["error"]!["kind"]!.GetValue<string>().ShouldBe("MemoryLimitExceeded");
        greedy["error"]!["code"]!.GetValue<string>().ShouldBe("OUT_OF_MEMORY", "the kernel enforced memory.max of the attempt cgroup");
        numpy["outputs"]!["threads"]!.GetValue<string>().ShouldBe(string.Empty, "with a cgroup the numeric libraries keep their threads");
    }
}
