using System.Diagnostics;

namespace PythonScriptRunner.Worker.Tests;

/// <summary>Spec 006: lost workers (leases), cancellation, graceful shutdown and queued reprocessing.</summary>
public sealed class RecoveryTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python)
{
    private static readonly TimeSpan ShortLease = TimeSpan.FromSeconds(4);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spec006_AC05_LostWorker_IdempotentStepIsRecoveredByAnother()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var (doomed, survivor, runId) = await StartAndLoseAWorkerAsync(env, idempotent: true);

        var lostAt = Stopwatch.GetTimestamp();
        var run = await survivor.WaitForRunAsync(runId, timeout: TimeSpan.FromSeconds(60));

        run.Status.ShouldBe(RunStatus.Succeeded);
        var attempts = run.Steps.Single().Attempts;
        attempts.Select(a => (a.Status, a.Cause, a.WorkerId)).ShouldBe(
        [
            (AttemptStatus.WorkerLost, AttemptCause.Initial, doomed.InstanceId),
            (AttemptStatus.Succeeded, AttemptCause.LeaseRecovery, survivor.InstanceId),
        ]);

        // Lease + reaper interval (the spec bound is LeaseDuration + 15 s with the default 60 s lease).
        Stopwatch.GetElapsedTime(lostAt).ShouldBeLessThan(ShortLease + TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Spec006_AC06_LostWorker_NonIdempotentStepFailsAndReprocessCompletesIt()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var (_, survivor, runId) = await StartAndLoseAWorkerAsync(env, idempotent: false);

        var failed = await survivor.WaitForRunAsync(runId, timeout: TimeSpan.FromSeconds(60));
        failed.Status.ShouldBe(RunStatus.Failed);
        failed.Steps.Single().Error!.Kind.ShouldBe(ErrorKind.WorkerLost);
        failed.Steps.Single().Attempts.Single().Status.ShouldBe(AttemptStatus.WorkerLost);

        await survivor.Runs.ReprocessAsync(runId, PsrEnvironment.Author, ExecutionMode.Async, Ct);
        var run = await survivor.WaitForRunAsync(runId, r => r.Status != RunStatus.Running);

        run.Status.ShouldBe(RunStatus.Succeeded);
        run.Steps.Single().Attempts.Select(a => a.Cause).ShouldBe([AttemptCause.Initial, AttemptCause.Reprocess]);
    }

    [Fact]
    public async Task Spec006_AC09_Cancel_StopsTheProcessInTheWorker()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var worker = await env.StartHostAsync();
        var app = await env.StartHostAsync(s => s.Worker = false);
        await PsrEnvironment.PublishStageAsync(app, "dorme", Scripts.Sleep, [Scripts.Number("segundos"), Scripts.Text("texto")], [Scripts.Text("texto")]);
        var handle = await app.Queue.EnqueueStageAsync("dorme", new { segundos = 120, texto = "longo" }, cancellationToken: Ct);
        await app.WaitForRunAsync(handle.RunId, r => r.Steps[0].Attempts.Count == 1);
        await Task.Delay(500, Ct); // the Python process is running

        var started = Stopwatch.GetTimestamp();
        await app.Runs.CancelAsync(handle.RunId, PsrEnvironment.Author, Ct);
        var run = await app.WaitForRunAsync(handle.RunId, timeout: TimeSpan.FromSeconds(10));

        Stopwatch.GetElapsedTime(started).ShouldBeLessThan(TimeSpan.FromSeconds(3));
        run.Status.ShouldBe(RunStatus.Cancelled);
        run.Steps.Single().Status.ShouldBe(StepStatus.Cancelled);
        run.Steps.Single().Attempts.Single().Status.ShouldBe(AttemptStatus.Cancelled);
        await worker.Events.WaitForAsync(RunEventTypes.RunCancelled, e => e.RunId == handle.RunId);
    }

    [Fact]
    public async Task Cancel_QueuedRun_EndsAtOnceAndFreesThePipelineSlot()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var app = await env.StartHostAsync(s => s.Worker = false);
        await PsrEnvironment.PublishStageAsync(app, "eco", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);
        await PsrEnvironment.PublishPipelineAsync(app, "unica", Scripts.SingleStep("eco", 1, [Scripts.Text("texto")]));
        var pipeline = (await app.Admin.GetPipelineAsync("unica", Ct))!;
        await app.Admin.UpdatePipelineSettingsAsync("unica", new PipelineSettings(1), pipeline.RowVersion, PsrEnvironment.Author, Ct);
        var first = await app.Queue.EnqueuePipelineAsync("unica", new { texto = "a" }, cancellationToken: Ct);
        var second = await app.Queue.EnqueuePipelineAsync("unica", new { texto = "b" }, cancellationToken: Ct);
        second.Status.ShouldBe(RunStatus.Pending);

        await app.Runs.CancelAsync(first.RunId, PsrEnvironment.Author, Ct);

        (await app.Runs.GetAsync(first.RunId, Ct))!.Status.ShouldBe(RunStatus.Cancelled);
        (await app.Runs.GetAsync(second.RunId, Ct))!.Status.ShouldBe(RunStatus.Queued);
        await env.StartHostAsync();
        (await app.WaitForRunAsync(second.RunId)).Status.ShouldBe(RunStatus.Succeeded);
        await Should.ThrowAsync<PsrDomainException>(() => app.Runs.CancelAsync(first.RunId, PsrEnvironment.Author, Ct));
    }

    [Fact]
    public async Task Spec006_AC15_Shutdown_FinishesTheStepInProgressAndStopsConsuming()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var worker = await env.StartHostAsync(s => s.ShutdownGracePeriod = TimeSpan.FromSeconds(60));
        var app = await env.StartHostAsync(s => s.Worker = false);
        await PsrEnvironment.PublishStageAsync(app, "dorme", Scripts.Sleep, [Scripts.Number("segundos"), Scripts.Text("texto")], [Scripts.Text("texto")]);
        var running = await app.Queue.EnqueueStageAsync("dorme", new { segundos = 5, texto = "em andamento" }, cancellationToken: Ct);
        await app.WaitForRunAsync(running.RunId, r => r.Steps[0].Attempts.Count == 1);

        var stopping = worker.StopAsync(Ct);
        await Task.Delay(500, Ct);
        var later = await app.Queue.EnqueueStageAsync("dorme", new { segundos = 0, texto = "depois" }, cancellationToken: Ct);
        await stopping;

        var finished = (await app.Runs.GetAsync(running.RunId, Ct))!;
        finished.Status.ShouldBe(RunStatus.Succeeded);
        finished.Steps.Single().Attempts.Single().Status.ShouldBe(AttemptStatus.Succeeded);
        (await env.ScalarAsync<long>("SELECT count(*) FROM psr.step_attempts WHERE status = 'Running'")).ShouldBe(0);
        await Task.Delay(1000, Ct);
        (await app.Runs.GetAsync(later.RunId, Ct))!.Status.ShouldBe(RunStatus.Queued);
    }

    [Fact]
    public async Task Spec003_AC10_ReprocessAsync_ResumesFromTheFailedStep()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var host = await env.StartHostAsync();
        await PsrEnvironment.PublishStageAsync(host, "conta", Scripts.Count, [Scripts.Text("marcador")], [Scripts.Integer("execucoes")]);
        await PsrEnvironment.PublishStageAsync(host, "falha-uma-vez", FailOnce, [Scripts.Text("marcador")], [Scripts.Text("texto")]);
        await PsrEnvironment.PublishPipelineAsync(host, "retoma", new PipelineDraftData
        {
            Inputs = [Scripts.Text("contador"), Scripts.Text("marcador")],
            Steps =
            [
                new PipelineStepDefinition("conta", "conta", 1) { Bindings = [new InputBinding("marcador", new PipelineInputSource("contador"))] },
                new PipelineStepDefinition("falha", "falha-uma-vez", 1) { Bindings = [new InputBinding("marcador", new PipelineInputSource("marcador"))] },
            ],
            Outputs = [new PipelineOutputDefinition("texto", new StepOutputSource("falha", "texto"))],
        });
        var folder = Path.Combine(env.WorkRoot, "markers");
        Directory.CreateDirectory(folder);
        var inputs = new { contador = Path.Combine(folder, "conta.txt"), marcador = Path.Combine(folder, "falha.txt") };

        var handle = await host.Queue.EnqueuePipelineAsync("retoma", inputs, cancellationToken: Ct);
        (await host.WaitForRunAsync(handle.RunId)).Status.ShouldBe(RunStatus.Failed);
        var queued = await host.Runs.ReprocessAsync(handle.RunId, PsrEnvironment.Author, ExecutionMode.Async, Ct);
        queued.Status.ShouldBe(RunStatus.Running);
        var run = await host.WaitForRunAsync(handle.RunId, r => r.Status != RunStatus.Running);

        run.Status.ShouldBe(RunStatus.Succeeded);
        run.Id.ShouldBe(handle.RunId);
        run.Steps[0].Attempts.Count.ShouldBe(1);
        run.Steps[0].Outputs!["execucoes"]!.GetValue<int>().ShouldBe(1);
        run.Steps[1].Attempts.Select(a => (a.Cause, a.Status)).ShouldBe([(AttemptCause.Initial, AttemptStatus.Failed), (AttemptCause.Reprocess, AttemptStatus.Succeeded)]);
        run.Steps[1].Attempts[1].TriggeredBy.ShouldBe(PsrEnvironment.Author.Subject);
    }

    private const string FailOnce = """
        import os

        from psrunner import StageError, stage


        @stage
        def run(inputs, ctx):
            if not os.path.exists(inputs["marcador"]):
                open(inputs["marcador"], "w").close()
                raise StageError("primeira tentativa falha", code="PRIMEIRA")
            return {"texto": "ok"}
        """;

    /// <summary>A worker takes the first attempt of a long step and is "killed" (no completion, no ack; D-109); a second worker keeps running.</summary>
    private static async Task<(PsrHost Doomed, PsrHost Survivor, RunId RunId)> StartAndLoseAWorkerAsync(PsrEnvironment env, bool idempotent)
    {
        void Leases(HostSettings s)
        {
            s.LeaseDuration = ShortLease;
            s.LeaseRenewInterval = TimeSpan.FromSeconds(1);
            s.ReaperInterval = TimeSpan.FromSeconds(1);
        }

        var doomed = await env.StartHostAsync(s =>
        {
            Leases(s);
            s.ShutdownGracePeriod = TimeSpan.Zero;
        });
        await PsrEnvironment.PublishStageAsync(doomed, "lenta", Scripts.SlowFirstAttempt, [], [Scripts.Integer("tentativa")], idempotent: idempotent);
        var handle = await doomed.Queue.EnqueueStageAsync("lenta", null, cancellationToken: Ct);
        await doomed.WaitForRunAsync(handle.RunId, r => r.Steps[0].Attempts.Count == 1);
        await Task.Delay(500, Ct);

        var survivor = await env.StartHostAsync(Leases);
        await doomed.StopAsync(Ct);
        return (doomed, survivor, handle.RunId);
    }
}
