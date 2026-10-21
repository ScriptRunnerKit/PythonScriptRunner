using System.Diagnostics;
using System.Text.Json.Nodes;
using PythonScriptRunner.Core.Execution;
using PythonScriptRunner.Core.Tests.Infrastructure;

namespace PythonScriptRunner.Core.Tests.Integration;

public sealed class IsolationTests(PythonFixture fixture)
{
    [Fact]
    [Trait("Category", "Timing")]
    public async Task Spec001_AC07_Timeout_KillsTheProcessTree()
    {
        // Arrange
        var pidFile = Path.Combine(Path.GetTempPath(), "psr-tests", $"child-{Guid.NewGuid():N}.pid");
        await using var host = RunnerHost.Create(fixture, b => b.AddStage("lento", s => s
            .Script("""
                import subprocess, sys, time
                from psrunner import stage

                @stage
                def run(inputs, ctx):
                    child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(120)"])
                    with open(inputs["pid_file"], "w") as f:
                        f.write(str(child.pid))
                    time.sleep(60)
                    return {}
                """)
            .Input("pid_file", FieldType.String)
            .Timeout(TimeSpan.FromSeconds(2))));
        var stopwatch = Stopwatch.StartNew();

        // Act
        await using var result = await host.Runner.RunStageAsync("lento", new { pid_file = pidFile }, cancellationToken: TestContext.Current.CancellationToken);
        stopwatch.Stop();

        // Assert
        result.Status.ShouldBe(RunStatus.Failed);
        result.Steps[0].Status.ShouldBe(StepStatus.TimedOut);
        result.Error!.Kind.ShouldBe(ErrorKind.Timeout);
        // Spec: the attempt ends in at most ~3 s with a 2 s timeout (small margin for slow CI runners).
        result.Steps[0].Attempts[0].Duration.ShouldBeLessThan(TimeSpan.FromSeconds(3.5));
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        (await IsProcessGoneAsync(int.Parse(await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken)))).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task Spec001_AC08_Cancellation_StopsTheRun()
    {
        await using var host = RunnerHost.Create(fixture, b => b.AddStage("espera", s => s.Script("""
            import time
            from psrunner import stage

            @stage
            def run(inputs, ctx):
                time.sleep(60)
                return {}
            """)));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(2));
        var stopwatch = Stopwatch.StartNew();

        await using var result = await host.Runner.RunStageAsync("espera", null, cancellationToken: cancellation.Token);

        result.Status.ShouldBe(RunStatus.Cancelled);
        result.Error!.Kind.ShouldBe(ErrorKind.Cancelled);
        // Cancelled after 2 s; the process tree must stop within ~2 s more.
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(4.5));
    }

    [Fact]
    public async Task Spec001_AC09_MemoryLimit_IsEnforced()
    {
        await using var host = RunnerHost.Create(fixture, b => b.AddStage("guloso", s => s
            .Script("""
                from psrunner import stage

                @stage
                def run(inputs, ctx):
                    data = bytearray(2 * 1024 * 1024 * 1024)
                    return {"tamanho": len(data)}
                """)
            .Output("tamanho", FieldType.Integer)
            .MemoryLimitMb(256)));

        await using var result = await host.Runner.RunStageAsync("guloso", null, cancellationToken: TestContext.Current.CancellationToken);

        result.Error!.Kind.ShouldBe(ErrorKind.MemoryLimitExceeded, result.Error.Message);
    }

    [Fact]
    public async Task Spec001_AC29_NumericLibraries_GetOneThreadWhenTheLimitIsRlimitAs_OrTheConfiguredCount()
    {
        const string script = """
            import os
            from psrunner import stage

            @stage
            def run(inputs, ctx):
                return {"threads": os.environ.get("OPENBLAS_NUM_THREADS", "") + "/" + os.environ.get("OMP_NUM_THREADS", "")}
            """;
        await using var standard = RunnerHost.Create(fixture, b => b.AddStage("threads", s => s.Script(script).Output("threads", FieldType.String)));
        await using var configured = RunnerHost.Create(
            fixture, b => b.AddStage("threads", s => s.Script(script).Output("threads", FieldType.String)), o => o.ThreadsPerProcess = 4);

        await using var byDefault = await standard.Runner.RunStageAsync("threads", null, cancellationToken: TestContext.Current.CancellationToken);
        await using var explicitly = await configured.Runner.RunStageAsync("threads", null, cancellationToken: TestContext.Current.CancellationToken);

        // The CI and developer machines have no delegable cgroup: Linux falls back to RLIMIT_AS (one thread each); Windows
        // limits committed memory with a Job Object and keeps the libraries' own default.
        byDefault.GetOutput<string>("threads").ShouldBe(OperatingSystem.IsLinux() ? "1/1" : "/");
        explicitly.GetOutput<string>("threads").ShouldBe("4/4");
    }

    [Fact]
    public async Task Spec001_AC10_Secrets_ReachTheScriptButNotLogsOrInputs()
    {
        // Arrange
        const string secret = "s3cr3t-valor";
        await using var host = RunnerHost.Create(
            fixture,
            b => b.AddStage("smtp", s => s
                .Script("""
                    import os
                    from psrunner import stage

                    @stage
                    def run(inputs, ctx):
                        senha = ctx.secrets["SMTP_SENHA"]
                        print("senha:", senha)
                        with open(os.environ["PSR_INPUT"], encoding="utf-8") as f:
                            vazou = senha in f.read()
                        return {"tamanho": len(senha), "vazou_no_input": vazou}
                    """)
                .Secret("SMTP_SENHA")
                .Output("tamanho", FieldType.Integer)
                .Output("vazou_no_input", FieldType.Bool)),
            configuration: new Dictionary<string, string?> { ["PythonScriptRunner:Secrets:SMTP_SENHA"] = secret });

        // Act
        await using var result = await host.Runner.RunStageAsync("smtp", null, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Status.ShouldBe(RunStatus.Succeeded, result.Error?.Message);
        result.GetOutput<int>("tamanho").ShouldBe(secret.Length);
        result.GetOutput<bool>("vazou_no_input").ShouldBeFalse();
        var stdout = result.Steps[0].Attempts[0].Stdout.Content;
        stdout.ShouldContain("senha: ***");
        stdout.ShouldNotContain(secret);
    }

    [Fact]
    public async Task Spec001_AC11_ProcessGetsACleanEnvironment()
    {
        Environment.SetEnvironmentVariable("PSR_TEST_HOST_ONLY", "1");
        await using var host = RunnerHost.Create(fixture, b => b.AddStage("ambiente", s => s
            .Script("""
                import os
                from psrunner import stage

                @stage
                def run(inputs, ctx):
                    return {"nomes": sorted(os.environ)}
                """)
            .Output("nomes", FieldType.Json)));

        await using var result = await host.Runner.RunStageAsync("ambiente", null, cancellationToken: TestContext.Current.CancellationToken);

        var names = result.GetOutput<JsonArray>("nomes").Select(n => n!.GetValue<string>().ToUpperInvariant()).ToList();
        names.ShouldNotContain("PSR_TEST_HOST_ONLY");
        // LC_CTYPE is set by the interpreter itself on Linux (PEP 538 locale coercion), not inherited from the host. The
        // thread variables come from the runner when the memory limit falls back to RLIMIT_AS (Linux without cgroup; D-180).
        string[] allowed = ["PATH", "SYSTEMROOT", "TEMP", "TMP", "TMPDIR", "HOME", "LC_CTYPE", .. ProcessPythonExecutor.ThreadVariables];
        names.Where(n => !n.StartsWith("PSR_", StringComparison.Ordinal) && !allowed.Contains(n)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Spec001_AC12_MissingSecret_FailsWithoutStartingPython()
    {
        await using var host = RunnerHost.Create(fixture, b => b.AddStage("smtp", s => s.Script(Scripts.Echo).Secret("NAO_CONFIGURADO")));

        await using var result = await host.Runner.RunStageAsync("smtp", null, cancellationToken: TestContext.Current.CancellationToken);

        result.Error!.Kind.ShouldBe(ErrorKind.SecretNotFound);
        result.Error.Message.ShouldContain("NAO_CONFIGURADO");
        result.Steps[0].Attempts[0].ExitCode.ShouldBeNull();
    }

    private static async Task<bool> IsProcessGoneAsync(int pid)
    {
        for (var i = 0; i < 30; i++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }
}
