using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace PythonScriptRunner.RabbitMQ.Tests;

/// <summary>Spec 005: the public contracts match the JSON Schemas published in <c>schemas/messages</c>.</summary>
public sealed class ContractSchemaTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python)
{
    // Schemas are registered globally by their $id, so each one is loaded once per test run.
    private static readonly ConcurrentDictionary<string, JsonSchema> Loaded = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PublishedEventsAndTheCommandExample_MatchTheSchemas()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var host = await env.StartHostAsync();
        await PsrEnvironment.PublishStageAsync(host, "eco", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);
        await PsrEnvironment.PublishPipelineAsync(host, "eco-contrato", Scripts.SingleStep("eco", 1, [Scripts.Text("texto")]));
        await PsrEnvironment.PublishStageAsync(host, "falha", Scripts.AlwaysFails, [], []);
        var succeeded = await host.Queue.EnqueuePipelineAsync("eco-contrato", new { texto = "ok" }, new RunOptions { CorrelationId = "contrato" }, Ct);
        var failed = await host.Queue.EnqueueStageAsync("falha", null, cancellationToken: Ct);
        await host.WaitForRunAsync(succeeded.RunId);
        await host.WaitForRunAsync(failed.RunId);
        await using (var raw = await RawBroker.ConnectAsync(env.Broker))
        {
            await raw.PublishAsync(RabbitMQ.PsrTopology.PublicExchange, RabbitMQ.PsrTopology.StartPipelineRoutingKey, """{"pipelineKey":"nao-existe"}""",
                PsrMessageTypes.StartPipelineRun);
        }

        await host.Events.WaitForAsync(RunEventTypes.RunRejected);
        var eventSchema = Schema("run-event.v1.json");
        var payloads = await env.ColumnAsync<string>("SELECT payload::text FROM psr.outbox_messages WHERE message_type = 'psr.run-event.v1'");

        payloads.Select(p => JsonNode.Parse(p)!["type"]!.GetValue<string>()).Distinct().ShouldBe(
            [
                RunEventTypes.RunQueued, RunEventTypes.RunStarted, RunEventTypes.StepStarted, RunEventTypes.StepSucceeded, RunEventTypes.RunSucceeded,
                RunEventTypes.StepFailed, RunEventTypes.RunFailed, RunEventTypes.RunRejected,
            ],
            ignoreOrder: true);
        foreach (var payload in payloads)
        {
            Validate(eventSchema, payload);
        }

        var commandSchema = Schema("start-pipeline-run.v1.json");
        Validate(commandSchema, """
            {
              "pipelineKey": "calculo-progressao",
              "version": null,
              "inputs": { "a1": 1, "razao": 2, "n": 4, "tipo": "geometrica", "coeficientes": [1, 0, 1] },
              "idempotencyKey": "pedido-123",
              "correlationId": "erp-77881",
              "requestedBy": "erp"
            }
            """);
        Validate(commandSchema,
            JsonSerializer.Serialize(new StartPipelineRunCommand { PipelineKey = "calculo-progressao", CorrelationId = "x" }, PsrMessageJson.Options));
    }

    private JsonSchema Schema(string name) =>
        Loaded.GetOrAdd(name, file => JsonSchema.FromFile(Path.Combine(python.RepositoryRoot, "schemas", "messages", file)));

    private static void Validate(JsonSchema schema, string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        var errors = ((IEnumerable<EvaluationResults>)[result, .. result.Details ?? []])
            .SelectMany(d => (d.Errors?.Values ?? Enumerable.Empty<string>()).Select(e => $"{d.InstanceLocation}: {e}"));
        result.IsValid.ShouldBeTrue($"{json} → {string.Join("; ", errors)}");
    }
}
