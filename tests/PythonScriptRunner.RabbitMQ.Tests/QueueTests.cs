using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PythonScriptRunner.EntityFrameworkCore;
using Testcontainers.RabbitMq;

namespace PythonScriptRunner.RabbitMQ.Tests;

/// <summary>Spec 005: queued runs, the outbox relay and delivery guarantees.</summary>
public sealed class QueueTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spec005_AC01_Enqueue_StoresRunStepAndMessageTogetherAndStartsQuickly()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var app = await env.StartHostAsync(s =>
        {
            s.Worker = false;
            s.Relay = false;
        });
        await PsrEnvironment.PublishStageAsync(app, "eco", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);

        var queued = await app.Queue.EnqueueStageAsync("eco", new { texto = "fila" }, cancellationToken: Ct);

        var run = (await app.Runs.GetAsync(queued.RunId, Ct))!;
        run.Status.ShouldBe(RunStatus.Queued);
        run.Mode.ShouldBe(ExecutionMode.Async);
        run.Steps.Single().Status.ShouldBe(StepStatus.Queued);
        (await env.ScalarAsync<long>(
            $"SELECT count(*) FROM psr.outbox_messages WHERE message_type = 'psr.execute-step.v1' AND payload->>'runId' = '{queued.RunId}' AND published_at IS NULL"))
            .ShouldBe(1);

        // With an idle worker, enqueue → start p95 ≤ 2 s over 100 runs (D-58).
        var worker = await env.StartHostAsync(s => s.MaxConcurrency = 8);
        var waits = new List<TimeSpan>();
        for (var i = 0; i < 100; i++)
        {
            var handle = await worker.Queue.EnqueueStageAsync("eco", new { texto = $"t{i}" }, cancellationToken: Ct);
            var started = await worker.WaitForRunAsync(handle.RunId, r => r.Steps[0].Attempts.Count > 0);
            waits.Add(started.Steps[0].Attempts[0].StartedAt - started.QueuedAt!.Value);
        }

        waits.Sort();
        waits[94].ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Spec005_AC02_BrokerDown_EnqueueSucceedsAndRunsWhenItComesBack()
    {
        var port = FreePort();
        await using var dedicated = new RabbitMqBuilder(BrokerFixture.Image).WithPortBinding(port, RabbitMqBuilder.RabbitMqPort).Build();
        await dedicated.StartAsync(Ct);
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var uri = new UriBuilder(dedicated.GetConnectionString()) { Port = port }.Uri.ToString();
        var host = await env.StartHostAsync(s => s.BrokerConnectionString = uri);
        await PsrEnvironment.PublishStageAsync(host, "eco", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);
        await Eventually.WaitAsync(() => Task.FromResult(host.Broker.IsConnected), TimeSpan.FromSeconds(10));

        await dedicated.StopAsync(Ct);
        var handle = await host.Queue.EnqueueStageAsync("eco", new { texto = "sem broker" }, cancellationToken: Ct);
        handle.Status.ShouldBe(RunStatus.Queued);
        await Task.Delay(1000, Ct);
        (await host.Runs.GetAsync(handle.RunId, Ct))!.Status.ShouldBe(RunStatus.Queued);

        await dedicated.StartAsync(Ct);
        var run = await host.WaitForRunAsync(handle.RunId, timeout: TimeSpan.FromMinutes(2));
        run.Status.ShouldBe(RunStatus.Succeeded);
    }

    [Fact]
    public async Task Spec005_AC03_DuplicateExecuteStep_RunsTheStepOnce()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var app = await env.StartHostAsync(s =>
        {
            s.Worker = false;
            s.Relay = false;
        });
        var marker = await PublishCounterAsync(env, app);
        var handle = await app.Queue.EnqueueStageAsync("conta", new { marcador = marker }, cancellationToken: Ct);
        var payload = await env.ScalarAsync<string>(
            $"SELECT payload::text FROM psr.outbox_messages WHERE message_type = 'psr.execute-step.v1' AND payload->>'runId' = '{handle.RunId}'");

        await using (var raw = await RawBroker.ConnectAsync(env.Broker))
        {
            await raw.PublishAsync(PsrTopology.CommandsExchange, PsrTopology.ExecuteStepRoutingKey, payload, PsrMessageTypes.ExecuteStep);
            await raw.PublishAsync(PsrTopology.CommandsExchange, PsrTopology.ExecuteStepRoutingKey, payload, PsrMessageTypes.ExecuteStep);
        }

        var worker = await env.StartHostAsync(s => s.Relay = false);
        var run = await worker.WaitForRunAsync(handle.RunId);
        await Task.Delay(1000, Ct);

        run.Status.ShouldBe(RunStatus.Succeeded);
        run.Steps.Single().Attempts.Count.ShouldBe(1);
        (await File.ReadAllLinesAsync(marker, Ct)).Length.ShouldBe(1);
    }

    [Fact]
    public async Task Spec005_AC04_InvalidMessage_GoesToTheDeadLetterQueueAndOthersContinue()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var host = await env.StartHostAsync();
        await PsrEnvironment.PublishStageAsync(host, "eco", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);
        await using var raw = await RawBroker.ConnectAsync(env.Broker);

        await raw.PublishAsync(PsrTopology.CommandsExchange, PsrTopology.ExecuteStepRoutingKey, "{ isto não é json", PsrMessageTypes.ExecuteStep);
        var handle = await host.Queue.EnqueueStageAsync("eco", new { texto = "depois" }, cancellationToken: Ct);

        (await host.WaitForRunAsync(handle.RunId)).Status.ShouldBe(RunStatus.Succeeded);
        string? dead = null;
        await Eventually.WaitAsync(async () => (dead = await raw.GetAsync(PsrTopology.DeadLetterQueue)) is not null, TimeSpan.FromSeconds(10));
        dead.ShouldBe("{ isto não é json");
    }

    [Fact]
    public async Task Spec005_AC10_LostConfirm_IsPublishedAgainAndDeduplicated()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var app = await env.StartHostAsync(s =>
        {
            s.Worker = false;
            s.Relay = false;
        });
        var marker = await PublishCounterAsync(env, app);
        var handle = await app.Queue.EnqueueStageAsync("conta", new { marcador = marker }, cancellationToken: Ct);

        // The broker receives the message, but the relay "crashes" before recording the confirm.
        await using (var raw = await RawBroker.ConnectAsync(env.Broker))
        {
            await Should.ThrowAsync<IOException>(() => app.Services.GetRequiredService<PsrOutbox>().DispatchAsync(100, async (messages, _) =>
            {
                foreach (var message in messages)
                {
                    await raw.PublishAsync(message.Exchange, message.RoutingKey, message.Payload, message.MessageType, message.Id.ToString());
                }

                throw new IOException("simulated crash before the confirm was recorded");
            }, Ct));
        }

        var worker = await env.StartHostAsync();
        var run = await worker.WaitForRunAsync(handle.RunId);
        await Eventually.WaitAsync(async () => await env.ScalarAsync<long>(
            $"SELECT count(*) FROM psr.outbox_messages WHERE payload->>'runId' = '{handle.RunId}' AND message_type = 'psr.execute-step.v1' AND published_at IS NULL") == 0,
            TimeSpan.FromSeconds(10));
        await Task.Delay(1000, Ct);

        run.Status.ShouldBe(RunStatus.Succeeded);
        run.Steps.Single().Attempts.Count.ShouldBe(1);
        (await File.ReadAllLinesAsync(marker, Ct)).Length.ShouldBe(1);
    }

    [Fact]
    public async Task Spec005_AC09_TraceParent_WorkerActivityIsInTheCallerTrace()
    {
        using var source = new ActivitySource("tests");
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is "PythonScriptRunner" or "tests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (captured)
                {
                    captured.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var host = await env.StartHostAsync();
        await PsrEnvironment.PublishStageAsync(host, "eco", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);

        ActivityTraceId traceId;
        RunHandle handle;
        using (var parent = source.StartActivity("chamador")!)
        {
            traceId = parent.TraceId;
            handle = await host.Queue.EnqueueStageAsync("eco", new { texto = "rastreado" }, cancellationToken: Ct);
        }

        (await host.WaitForRunAsync(handle.RunId)).Status.ShouldBe(RunStatus.Succeeded);
        await Eventually.WaitAsync(() => Task.FromResult(Snapshot().Any(a => a.OperationName == "psr.stage.execute" && a.TraceId == traceId)),
            TimeSpan.FromSeconds(10));
        var consume = Snapshot().Single(a => a.OperationName == "psr.message.consume" && a.TraceId == traceId && (string?)a.GetTagItem("psr.run.id") == handle.RunId.ToString());
        Snapshot().Single(a => a.OperationName == "psr.stage.execute" && a.TraceId == traceId).ParentSpanId.ShouldBe(consume.SpanId);

        List<Activity> Snapshot()
        {
            lock (captured)
            {
                return [.. captured];
            }
        }
    }

    [Fact]
    public async Task Spec005_AC12_Topology_IsDeclaredIdempotentlyOrValidated()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);

        var notDeclared = env.CreateHost(s =>
        {
            s.Worker = false;
            s.DeclareTopology = false;
        });
        var error = await Should.ThrowAsync<PsrConfigurationException>(() => notDeclared.Host.StartAsync(Ct));
        error.Message.ShouldContain("psr.step.execute");
        error.Message.ShouldContain("DeclareTopology");

        await env.StartHostAsync(s => s.Worker = false);
        await env.StartHostAsync(s => s.Worker = false);
        var validated = await env.StartHostAsync(s =>
        {
            s.Worker = false;
            s.DeclareTopology = false;
        });
        await Eventually.WaitAsync(() => Task.FromResult(validated.Broker.IsConnected), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Sync_RunWithTheQueueRegistered_PublishesItsEvents()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var host = await env.StartHostAsync(s => s.Worker = false);
        await PsrEnvironment.PublishStageAsync(host, "eco", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);

        await using var result = await host.Runner.RunStageAsync("eco", new { texto = "síncrono" }, new RunOptions { CorrelationId = "sync-1" }, Ct);

        var succeeded = await host.Events.WaitForAsync(RunEventTypes.RunSucceeded, e => e.RunId == result.Id);
        succeeded.CorrelationId.ShouldBe("sync-1");
        succeeded.Outputs!["texto"]!.GetValue<string>().ShouldBe("síncrono");
        host.Events.For(result.Id).Select(e => e.Type).ShouldBe(
            [RunEventTypes.RunStarted, RunEventTypes.StepStarted, RunEventTypes.StepSucceeded, RunEventTypes.RunSucceeded], ignoreOrder: true);
        (await host.Runs.GetAsync(result.Id, Ct))!.Mode.ShouldBe(ExecutionMode.Sync);
    }

    private static async Task<string> PublishCounterAsync(PsrEnvironment env, PsrHost host)
    {
        await PsrEnvironment.PublishStageAsync(host, "conta", Scripts.Count, [Scripts.Text("marcador")], [Scripts.Integer("execucoes")]);
        var folder = Path.Combine(env.WorkRoot, "markers");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{Guid.NewGuid():N}.txt");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
