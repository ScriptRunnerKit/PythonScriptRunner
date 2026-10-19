using PythonScriptRunner.Core.Tests.Infrastructure;

namespace PythonScriptRunner.Core.Tests.Integration;

public sealed class EnvironmentTests(PythonFixture fixture)
{
    private const string Ok = """
        from psrunner import stage

        @stage
        def run(inputs, ctx):
            return {}
        """;

    [Fact]
    public async Task Spec001_AC14_ConcurrentFirstUse_CreatesTheEnvironmentOnce()
    {
        // Arrange: a private cache so the environment does not exist yet.
        var cache = Path.Combine(Path.GetTempPath(), "psr-tests", "isolated-cache", Guid.NewGuid().ToString("N"));
        await using var host = RunnerHost.Create(
            fixture,
            b => b.AddStage("com-dependencia", s => s.Script(Ok).Requirements("six\n")),
            o => o.EnvironmentCacheRoot = cache);
        var manager = host.Services.GetRequiredService<IEnvironmentManager>();
        var stage = await host.Services.GetRequiredService<ICatalog>().FindStageAsync("com-dependencia", null, TestContext.Current.CancellationToken);

        // Act
        var environments = await Task.WhenAll(manager.EnsureAsync(stage!, TestContext.Current.CancellationToken), manager.EnsureAsync(stage!, TestContext.Current.CancellationToken));
        await using var first = await host.Runner.RunStageAsync("com-dependencia", null, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        environments.Count(e => e.Created).ShouldBe(1);
        environments.Select(e => e.Directory).Distinct().ShouldHaveSingleItem();
        first.Status.ShouldBe(RunStatus.Succeeded, first.Error?.Message);
    }

    [Fact]
    public async Task Spec001_AC15_UnknownDependency_IsEnvironmentSetupFailed()
    {
        await using var host = RunnerHost.Create(fixture, b => b.AddStage("quebrado", s => s
            .Script(Ok)
            .Requirements("psr-pacote-que-nao-existe-123456==1.0\n")));

        await using var result = await host.Runner.RunStageAsync("quebrado", null, cancellationToken: TestContext.Current.CancellationToken);

        result.Error!.Kind.ShouldBe(ErrorKind.EnvironmentSetupFailed);
        result.Error.Message.ShouldContain("uv pip");
    }
}

[Collection(SerialCollection.Name)]
public sealed class EnvironmentCacheTests(PythonFixture fixture)
{
    [Fact]
    public async Task Spec001_AC13_StagesWithSameDependencies_ShareTheVenv_AndOverheadIsLow()
    {
        // Arrange
        const string script = """
            from psrunner import stage

            @stage
            def run(inputs, ctx):
                return {"ok": True}
            """;
        await using var host = RunnerHost.Create(fixture, b => b
            .AddStage("primeiro", s => s.Script(script).Output("ok", FieldType.Bool))
            .AddStage("segundo", s => s.Script(script).Output("ok", FieldType.Bool)));

        // Act
        var overheads = new List<TimeSpan>();
        var hashes = new HashSet<string?>();
        for (var i = 0; i < 50; i++)
        {
            await using var result = await host.Runner.RunStageAsync(i % 2 == 0 ? "primeiro" : "segundo", null, cancellationToken: TestContext.Current.CancellationToken);
            result.EnsureSuccess();
            var attempt = result.Steps[0].Attempts[0];
            overheads.Add(attempt.Overhead);
            hashes.Add(attempt.EnvironmentHash);
        }

        // Assert
        hashes.ShouldHaveSingleItem();
        var p95 = overheads.Order().ElementAt((int)Math.Ceiling(overheads.Count * 0.95) - 1);
        p95.ShouldBeLessThanOrEqualTo(TimeSpan.FromMilliseconds(300));
    }
}
