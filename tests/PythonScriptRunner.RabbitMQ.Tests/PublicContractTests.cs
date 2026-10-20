using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace PythonScriptRunner.RabbitMQ.Tests;

/// <summary>Spec 005: the public StartPipelineRun command and run event subscriptions (D-72, D-35, D-110).</summary>
public sealed class PublicContractTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spec005_AC05_StartPipelineRun_CreatesAQueueCommandRunWithTheCorrelation()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var host = await env.StartHostAsync();
        await PublishEchoPipelineAsync(host);

        await SendAsync(env, new JsonObject
        {
            ["pipelineKey"] = "eco-publica",
            ["inputs"] = new JsonObject { ["texto"] = "de fora" },
            ["correlationId"] = "erp-77881",
            ["requestedBy"] = "erp",
        });

        var queued = await host.Events.WaitForAsync(RunEventTypes.RunQueued, e => e.CorrelationId == "erp-77881");
        var run = await host.WaitForRunAsync(queued.RunId!.Value);
        run.Status.ShouldBe(RunStatus.Succeeded);
        run.Trigger.ShouldBe(RunTrigger.QueueCommand);
        run.TriggeredBy.ShouldBe("queue:erp");
        run.CorrelationId.ShouldBe("erp-77881");
        var succeeded = await host.Events.WaitForAsync(RunEventTypes.RunSucceeded, e => e.RunId == run.Id);
        succeeded.CorrelationId.ShouldBe("erp-77881");
        succeeded.Outputs!["texto"]!.GetValue<string>().ShouldBe("de fora");
    }

    [Fact]
    public async Task Spec005_AC06_InvalidStartPipelineRun_IsRejectedWithReasonsAndCreatesNoRun()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var host = await env.StartHostAsync();
        await PublishEchoPipelineAsync(host);
        await PsrEnvironment.PublishStageAsync(host, "le-arquivo", "from psrunner import stage\n\n\n@stage\ndef run(inputs, ctx):\n    return {}\n",
            [Scripts.File("arquivo")], []);
        await PsrEnvironment.PublishPipelineAsync(host, "com-arquivo", new PipelineDraftData
        {
            Inputs = [Scripts.File("arquivo")],
            Steps = [new PipelineStepDefinition("le", "le-arquivo", 1)],
        });

        await SendAsync(env, new JsonObject { ["pipelineKey"] = "nao-existe", ["correlationId"] = "c-1" });
        await SendAsync(env, new JsonObject { ["pipelineKey"] = "eco-publica", ["inputs"] = new JsonObject { ["texto"] = 42 }, ["correlationId"] = "c-2" });
        await SendAsync(env, new JsonObject { ["pipelineKey"] = "com-arquivo", ["correlationId"] = "c-3" });

        var notFound = await host.Events.WaitForAsync(RunEventTypes.RunRejected, e => e.CorrelationId == "c-1");
        var invalid = await host.Events.WaitForAsync(RunEventTypes.RunRejected, e => e.CorrelationId == "c-2");
        var withFile = await host.Events.WaitForAsync(RunEventTypes.RunRejected, e => e.CorrelationId == "c-3");
        notFound.Error!.Code.ShouldBe("PIPELINE_NOT_FOUND");
        notFound.RunId.ShouldBeNull();
        invalid.Error!.Code.ShouldBe("INPUT_INVALID");
        invalid.Error.Reasons!.ShouldContain(r => r.StartsWith("texto", StringComparison.Ordinal));
        withFile.Error!.Code.ShouldBe(PsrErrorCodes.FileInputNotAllowed);
        withFile.Error.Reasons!.Single().ShouldContain("arquivo");
        (await env.ScalarAsync<long>("SELECT count(*) FROM psr.runs")).ShouldBe(0);
    }

    [Fact]
    public async Task Spec005_AC07_SameIdempotencyKey_CreatesOneRun()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var host = await env.StartHostAsync();
        await PublishEchoPipelineAsync(host);
        var command = new JsonObject
        {
            ["pipelineKey"] = "eco-publica",
            ["inputs"] = new JsonObject { ["texto"] = "uma vez" },
            ["idempotencyKey"] = "pedido-123",
            ["correlationId"] = "dup",
        };

        await SendAsync(env, command);
        await SendAsync(env, command);

        await Eventually.WaitAsync(() => Task.FromResult(host.Events.All.Count(e => e.Type == RunEventTypes.RunQueued && e.CorrelationId == "dup") == 2),
            TimeSpan.FromSeconds(30));
        host.Events.All.Where(e => e.Type == RunEventTypes.RunQueued && e.CorrelationId == "dup").Select(e => e.RunId).Distinct().Count().ShouldBe(1);
        (await env.ScalarAsync<long>("SELECT count(*) FROM psr.runs")).ShouldBe(1);
    }

    [Fact]
    public async Task StartPipelineRun_RedeliveredWithoutIdempotencyKey_IsDeduplicatedByMessageId()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var host = await env.StartHostAsync();
        await PublishEchoPipelineAsync(host);
        var command = new JsonObject { ["pipelineKey"] = "eco-publica", ["inputs"] = new JsonObject { ["texto"] = "x" }, ["correlationId"] = "msg" };
        var messageId = Guid.NewGuid().ToString();

        await SendAsync(env, command, messageId);
        await SendAsync(env, command, messageId);

        await Eventually.WaitAsync(() => Task.FromResult(host.Events.All.Count(e => e.Type == RunEventTypes.RunQueued && e.CorrelationId == "msg") == 2),
            TimeSpan.FromSeconds(30));
        (await env.ScalarAsync<long>("SELECT count(*) FROM psr.runs")).ShouldBe(1);
    }

    [Fact]
    public async Task Spec005_AC08_Subscriber_ReceivesOnlyTheSuccessesOfItsPipelineWithOutputs()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var host = await env.StartHostAsync(s =>
        {
            s.Services = services => services.AddSingleton<SubscriptionRecorder>();
            s.Builder = psr => psr.AddRunEventHandler<SubscriptionRecorder>(sub => sub.Subscription("calculo").On("run.succeeded.calculo-progressao"));
        });
        await env.ImportFixtureAsync(host, "calculo-progressao");
        await PublishEchoPipelineAsync(host);
        await PsrEnvironment.PublishStageAsync(host, "falha", Scripts.AlwaysFails, [], []);
        var inputs = new { a1 = 1, razao = 2, n = 4, tipo = "geometrica", coeficientes = new[] { 1, 0, 1 } };

        var runs = new[]
        {
            await host.Queue.EnqueuePipelineAsync("calculo-progressao", inputs, cancellationToken: Ct),
            await host.Queue.EnqueuePipelineAsync("calculo-progressao", inputs, cancellationToken: Ct),
            await host.Queue.EnqueuePipelineAsync("eco-publica", new { texto = "outra" }, cancellationToken: Ct),
            await host.Queue.EnqueueStageAsync("falha", null, cancellationToken: Ct),
        };
        foreach (var run in runs)
        {
            await host.WaitForRunAsync(run.RunId, timeout: TimeSpan.FromMinutes(3));
        }

        await Task.Delay(1500, Ct);
        var received = host.Services.GetRequiredService<SubscriptionRecorder>().Events;
        received.Select(e => e.RunId!.Value.Value).ShouldBe([runs[0].RunId.Value, runs[1].RunId.Value], ignoreOrder: true);
        received.ShouldAllBe(e => e.Type == RunEventTypes.RunSucceeded && e.PipelineKey == "calculo-progressao");
        received.ShouldAllBe(e => e.Outputs!["soma"]!.GetValue<double>() == 89 && !e.OutputsTruncated);
    }

    [Fact]
    public async Task Spec005_AC11_EventHandler_ReceivesTheConfiguredPatterns()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var host = await env.StartHostAsync(s =>
        {
            s.Services = services => services.AddSingleton<SubscriptionRecorder>();
            s.Builder = psr => psr.AddRunEventHandler<SubscriptionRecorder>(sub => sub.Subscription("padroes").On("run.*.eco-publica", "step.succeeded.#"));
        });
        await PublishEchoPipelineAsync(host);

        var handle = await host.Queue.EnqueuePipelineAsync("eco-publica", new { texto = "padrões" }, cancellationToken: Ct);
        await host.WaitForRunAsync(handle.RunId);
        await Task.Delay(1500, Ct);

        host.Services.GetRequiredService<SubscriptionRecorder>().Events.Select(e => e.Type).ShouldBe(
            [RunEventTypes.RunQueued, RunEventTypes.RunStarted, RunEventTypes.StepSucceeded, RunEventTypes.RunSucceeded], ignoreOrder: true);
    }

    [Fact]
    public async Task PerInstanceSubscription_EveryInstanceReceivesEveryEvent()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var first = await env.StartHostAsync(s => s.Worker = false);
        var second = await env.StartHostAsync();
        await PublishEchoPipelineAsync(first);

        var handle = await first.Queue.EnqueuePipelineAsync("eco-publica", new { texto = "todos" }, cancellationToken: Ct);

        await first.Events.WaitForAsync(RunEventTypes.RunSucceeded, e => e.RunId == handle.RunId);
        await second.Events.WaitForAsync(RunEventTypes.RunSucceeded, e => e.RunId == handle.RunId);
    }

    private static async Task PublishEchoPipelineAsync(PsrHost host)
    {
        await PsrEnvironment.PublishStageAsync(host, "eco", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);
        await PsrEnvironment.PublishPipelineAsync(host, "eco-publica", Scripts.SingleStep("eco", 1, [Scripts.Text("texto")]));
    }

    private static async Task SendAsync(PsrEnvironment env, JsonObject command, string? messageId = null)
    {
        await using var raw = await RawBroker.ConnectAsync(env.Broker);
        await raw.PublishAsync(PsrTopology.PublicExchange, PsrTopology.StartPipelineRoutingKey, command.ToJsonString(), PsrMessageTypes.StartPipelineRun,
            messageId);
    }

    /// <summary>Events of a durable subscription.</summary>
    public sealed class SubscriptionRecorder : IRunEventHandler
    {
        private readonly ConcurrentQueue<RunEvent> _events = new();

        public IReadOnlyList<RunEvent> Events => [.. _events];

        public Task HandleAsync(RunEvent runEvent, CancellationToken cancellationToken)
        {
            _events.Enqueue(runEvent);
            return Task.CompletedTask;
        }
    }
}
