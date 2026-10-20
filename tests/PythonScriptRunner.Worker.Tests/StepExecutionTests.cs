using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;

namespace PythonScriptRunner.Worker.Tests;

/// <summary>Spec 006: execution of queued steps (one message per step, retries, concurrency).</summary>
public sealed class StepExecutionTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task QueuedStage_RunsInTheWorker()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var host = await env.StartHostAsync();
        await PsrEnvironment.PublishStageAsync(host, "eco", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);

        var handle = await host.Queue.EnqueueStageAsync("eco", new { texto = "olá" }, cancellationToken: Ct);

        handle.Status.ShouldBe(RunStatus.Queued);
        var run = await host.WaitForRunAsync(handle.RunId);
        run.Status.ShouldBe(RunStatus.Succeeded);
        run.Mode.ShouldBe(ExecutionMode.Async);
        run.Outputs!["texto"]!.GetValue<string>().ShouldBe("olá");
        run.Steps.Single().Attempts.Single().WorkerId.ShouldBe(host.InstanceId);
    }

    [Fact]
    public async Task Spec006_AC01_CalculoProgressao_RunsOneMessagePerStep()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var host = await env.StartHostAsync();
        await env.ImportFixtureAsync(host, "calculo-progressao");
        var inputs = new { a1 = 1, razao = 2, n = 4, tipo = "geometrica", coeficientes = new[] { 1, 0, 1 } };

        var handle = await host.Queue.EnqueuePipelineAsync("calculo-progressao", inputs, cancellationToken: Ct);
        var run = await host.WaitForRunAsync(handle.RunId, timeout: TimeSpan.FromMinutes(3));

        run.Status.ShouldBe(RunStatus.Succeeded, run.Error?.Message);
        await using var sync = await host.Runner.RunPipelineAsync("calculo-progressao", inputs, cancellationToken: Ct);
        JsonNode.DeepEquals(run.Outputs, sync.Outputs).ShouldBeTrue($"{run.Outputs} != {sync.Outputs}");
        run.Outputs!["soma"]!.GetValue<double>().ShouldBe(89);
        (await env.ScalarAsync<long>(
            $"SELECT count(*) FROM psr.outbox_messages WHERE message_type = 'psr.execute-step.v1' AND payload->>'runId' = '{handle.RunId}'"))
            .ShouldBe(4);

        await host.Events.WaitForAsync(RunEventTypes.RunSucceeded, e => e.RunId == handle.RunId);
        // Events written in the same transaction share the instant; ties follow the logical order.
        string[] logical = [RunEventTypes.RunQueued, RunEventTypes.RunStarted, RunEventTypes.StepStarted, RunEventTypes.StepSucceeded, RunEventTypes.RunSucceeded];
        var events = host.Events.For(handle.RunId).OrderBy(e => e.OccurredAt).ThenBy(e => Array.IndexOf(logical, e.Type)).ToList();
        events.Select(e => e.Type).ShouldBe(
        [
            RunEventTypes.RunQueued, RunEventTypes.RunStarted, RunEventTypes.StepStarted, RunEventTypes.StepSucceeded,
            RunEventTypes.StepStarted, RunEventTypes.StepSucceeded, RunEventTypes.StepStarted, RunEventTypes.StepSucceeded,
            RunEventTypes.StepStarted, RunEventTypes.StepSucceeded, RunEventTypes.RunSucceeded,
        ]);
        events[^1].Outputs!["soma"]!.GetValue<double>().ShouldBe(89);
    }

    [Fact]
    public async Task Spec006_AC03_TwoWorkers_ShareTheStepsAndRunEachOnce()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var first = await env.StartHostAsync(s => s.MaxConcurrency = 2);
        var second = await env.StartHostAsync(s => s.MaxConcurrency = 2);
        await PsrEnvironment.PublishStageAsync(first, "dorme", Scripts.Sleep, [Scripts.Number("segundos"), Scripts.Text("texto")], [Scripts.Text("texto")]);

        var handles = new List<RunHandle>();
        for (var i = 0; i < 20; i++)
        {
            handles.Add(await first.Queue.EnqueueStageAsync("dorme", new { segundos = 0.3, texto = $"t{i}" }, cancellationToken: Ct));
        }

        var runs = new List<RunSnapshot>();
        foreach (var handle in handles)
        {
            runs.Add(await first.WaitForRunAsync(handle.RunId, timeout: TimeSpan.FromMinutes(2)));
        }

        runs.ShouldAllBe(r => r.Status == RunStatus.Succeeded);
        runs.ShouldAllBe(r => r.Steps.Single().Attempts.Count == 1);
        runs.Select(r => r.Steps.Single().Attempts.Single().WorkerId).Distinct().Order()
            .ShouldBe(new[] { first.InstanceId, second.InstanceId }.Order());
    }

    [Fact]
    public async Task Spec006_AC04_Retry_WaitsTheBackoffBetweenAttempts()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python, time);
        var host = await env.StartHostAsync();
        await PsrEnvironment.PublishStageAsync(host, "falha", Scripts.AlwaysFails, [], [], retry: new RetryPolicy(2, TimeSpan.FromSeconds(10), 2.0));

        var handle = await host.Queue.EnqueueStageAsync("falha", null, cancellationToken: Ct);
        await host.WaitForRunAsync(handle.RunId, r => Finished(r) == 1 && r.Steps[0].Status == StepStatus.Queued);
        time.Advance(TimeSpan.FromSeconds(10));
        await host.WaitForRunAsync(handle.RunId, r => Finished(r) == 2 && r.Steps[0].Status == StepStatus.Queued);
        time.Advance(TimeSpan.FromSeconds(20));
        var run = await host.WaitForRunAsync(handle.RunId);

        run.Status.ShouldBe(RunStatus.Failed);
        var attempts = run.Steps.Single().Attempts;
        attempts.Select(a => a.Cause).ShouldBe([AttemptCause.Initial, AttemptCause.AutoRetry, AttemptCause.AutoRetry]);
        (attempts[1].StartedAt - attempts[0].FinishedAt!.Value).ShouldBe(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
        (attempts[2].StartedAt - attempts[1].FinishedAt!.Value).ShouldBe(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1));
        var retries = host.Events.For(handle.RunId).Where(e => e.Type == RunEventTypes.StepRetryScheduled).OrderBy(e => e.OccurredAt).ToList();
        retries.Select(e => e.RetryAt - e.OccurredAt).ShouldBe([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)]);

        static int Finished(RunSnapshot run) => run.Steps[0].Attempts.Count(a => a.Status == AttemptStatus.Failed);
    }

    [Fact]
    public async Task Spec006_AC07_PipelineLimit_KeepsTheOthersPendingInOrder()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var host = await env.StartHostAsync();
        await PsrEnvironment.PublishStageAsync(host, "dorme", Scripts.Sleep, [Scripts.Number("segundos"), Scripts.Text("texto")], [Scripts.Text("texto")]);
        await PsrEnvironment.PublishPipelineAsync(host, "limitada", Scripts.SingleStep("dorme", 1, [Scripts.Number("segundos"), Scripts.Text("texto")]));
        var pipeline = (await host.Admin.GetPipelineAsync("limitada", Ct))!;
        await host.Admin.UpdatePipelineSettingsAsync("limitada", new PipelineSettings(1), pipeline.RowVersion, PsrEnvironment.Author, Ct);

        var handles = new List<RunHandle>();
        for (var i = 0; i < 3; i++)
        {
            handles.Add(await host.Queue.EnqueuePipelineAsync("limitada", new { segundos = 1, texto = $"t{i}" }, cancellationToken: Ct));
        }

        handles.Select(h => h.Status).ShouldBe([RunStatus.Queued, RunStatus.Pending, RunStatus.Pending]);
        var runs = new List<RunSnapshot>();
        foreach (var handle in handles)
        {
            runs.Add(await host.WaitForRunAsync(handle.RunId, timeout: TimeSpan.FromMinutes(2)));
        }

        runs.ShouldAllBe(r => r.Status == RunStatus.Succeeded);
        runs[1].StartedAt!.Value.ShouldBeGreaterThanOrEqualTo(runs[0].FinishedAt!.Value);
        runs[2].StartedAt!.Value.ShouldBeGreaterThanOrEqualTo(runs[1].FinishedAt!.Value);
    }

    [Fact]
    public async Task Spec006_AC08_MaxConcurrency_LimitsTheProcessesOfAWorker()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var host = await env.StartHostAsync(s => s.MaxConcurrency = 2);
        await PsrEnvironment.PublishStageAsync(host, "dorme", Scripts.Sleep, [Scripts.Number("segundos"), Scripts.Text("texto")], [Scripts.Text("texto")]);

        var handles = new List<RunHandle>();
        for (var i = 0; i < 5; i++)
        {
            handles.Add(await host.Queue.EnqueueStageAsync("dorme", new { segundos = 2, texto = $"t{i}" }, cancellationToken: Ct));
        }

        var attempts = new List<AttemptSnapshot>();
        foreach (var handle in handles)
        {
            var run = await host.WaitForRunAsync(handle.RunId, timeout: TimeSpan.FromMinutes(2));
            run.Status.ShouldBe(RunStatus.Succeeded);
            attempts.Add(run.Steps.Single().Attempts.Single());
        }

        var peak = attempts.Max(a => attempts.Count(other => other.StartedAt < a.FinishedAt && a.StartedAt < other.FinishedAt));
        peak.ShouldBe(2, string.Join(" | ", attempts.Select(a => $"{a.StartedAt:HH:mm:ss.ffffff}-{a.FinishedAt:HH:mm:ss.ffffff} {a.WorkerId}")));
    }

    [Fact]
    public async Task Spec006_AC17_QueueWait_P95IsAtMostTwoSeconds()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var host = await env.StartHostAsync(s => s.MaxConcurrency = 8);
        using var waits = new MetricCollector<double>(host.Services.GetRequiredService<IMeterFactory>(), "PythonScriptRunner", "psr.queue.wait");
        await PsrEnvironment.PublishStageAsync(host, "eco", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);

        for (var i = 0; i < 100; i++)
        {
            var handle = await host.Queue.EnqueueStageAsync("eco", new { texto = $"t{i}" }, cancellationToken: Ct);
            await host.WaitForRunAsync(handle.RunId, r => r.Status != RunStatus.Queued);
        }

        await Eventually.WaitAsync(() => Task.FromResult(waits.GetMeasurementSnapshot().Count >= 100), TimeSpan.FromSeconds(30));
        var sorted = waits.GetMeasurementSnapshot().Select(m => m.Value).Order().ToList();
        sorted[(int)Math.Ceiling(sorted.Count * 0.95) - 1].ShouldBeLessThanOrEqualTo(2000);
    }
}
