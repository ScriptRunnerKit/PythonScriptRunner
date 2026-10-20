using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using PythonScriptRunner.Dashboard.Tests.Infrastructure;

namespace PythonScriptRunner.Dashboard.Tests;

/// <summary>Spec 007, AC-11: run updates reach the browsers of every dashboard instance in ≤ 1 s.</summary>
public sealed class RealtimeTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spec007_AC11_SubscribedClientsOfTwoInstances_ReceiveStepAndRunUpdatesInUnderOneSecond()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await env.StartHostAsync(s => s.RecordEvents = false);
        await using var first = await StartAsync(env);
        await using var second = await StartAsync(env);
        await first.App.PublishStageAsync("lento", Scripts.Sleep, [Scripts.Number("segundos"), Scripts.Text("texto")], [Scripts.Text("texto")]);
        await using var byStage = await Browser.ConnectAsync(first.App);
        await using var byRun = await Browser.ConnectAsync(second.App);
        await byStage.Connection.InvokeAsync("SubscribeStage", "lento", Ct);
        using var executor = first.App.CreateClient();
        executor.DefaultRequestHeaders.Add(HeaderFilter.Header, "ana|Executor");

        var accepted = await executor.PostAsJsonAsync("/psr/api/stages/lento/runs", new { inputs = new { segundos = 1.5, texto = "oi" } }, Ct);
        var runId = (await accepted.JsonAsync())["runId"]!.GetValue<string>();
        await byRun.Connection.InvokeAsync("SubscribeRun", runId, Ct);

        foreach (var (browser, instance) in ((Browser, Instance)[])[(byStage, first), (byRun, second)])
        {
            var finished = await browser.WaitForRunAsync(runId, "run.succeeded");
            finished.Message.GetProperty("run").GetProperty("status").GetString().ShouldBe("Succeeded");
            finished.Message.GetProperty("currentStep").GetString().ShouldBe("lento");
            var delivered = instance.Recorder.ReceivedAt(runId, "run.succeeded");
            Stopwatch.GetElapsedTime(delivered, finished.At).ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(1));

            var step = await browser.WaitForStepAsync(runId, "Succeeded");
            step.GetProperty("stepKey").GetString().ShouldBe("lento");
            step.GetProperty("order").GetInt32().ShouldBe(1);
            step.GetProperty("attempt").GetInt32().ShouldBe(1);
        }

        var started = await byStage.WaitForStepAsync(runId, "Running");
        started.GetProperty("finishedAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static async Task<Instance> StartAsync(PsrEnvironment env)
    {
        var recorder = new TimedRecorder();
        var app = await DashboardApp.StartAsync(env, s =>
        {
            s.Dashboard = o => o.UseAuthorizationFilter<HeaderFilter>();
            s.Builder = b => b.AddRunEventHandler<TimedRecorder>(r => r.PerInstance("timing"));
            s.Services = services => services.AddSingleton(recorder);
        });
        return new Instance(app, recorder);
    }

    private sealed record Instance(DashboardApp App, TimedRecorder Recorder) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>When each event reached the instance (same moment the dashboard bridge receives it, on another queue).</summary>
    private sealed class TimedRecorder : IRunEventHandler
    {
        private readonly ConcurrentDictionary<(string, string), long> _received = new();

        public Task HandleAsync(RunEvent runEvent, CancellationToken cancellationToken)
        {
            _received.TryAdd((runEvent.RunId?.ToString() ?? string.Empty, runEvent.Type), Stopwatch.GetTimestamp());
            return Task.CompletedTask;
        }

        public long ReceivedAt(string runId, string type) => _received[(runId, type)];
    }

    /// <summary>A SignalR client of the runs hub (long polling through the test server).</summary>
    private sealed class Browser : IAsyncDisposable
    {
        private readonly ConcurrentQueue<(long At, JsonElement Message)> _runs = new();
        private readonly ConcurrentQueue<JsonElement> _steps = new();

        private Browser(HubConnection connection)
        {
            Connection = connection;
            connection.On<JsonElement>("RunUpdated", message => _runs.Enqueue((Stopwatch.GetTimestamp(), message)));
            connection.On<JsonElement>("StepUpdated", _steps.Enqueue);
        }

        public HubConnection Connection { get; }

        public static async Task<Browser> ConnectAsync(DashboardApp app)
        {
            var connection = new HubConnectionBuilder()
                .WithUrl(new Uri(app.Server.BaseAddress, "/psr/hubs/runs"), o =>
                {
                    o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                    o.Transports = HttpTransportType.LongPolling;
                    o.Headers[HeaderFilter.Header] = "bia|Reader";
                })
                .Build();
            var browser = new Browser(connection);
            await connection.StartAsync(Ct);
            return browser;
        }

        public async Task<(long At, JsonElement Message)> WaitForRunAsync(string runId, string eventType)
        {
            (long, JsonElement) found = default;
            await Eventually.WaitAsync(() => Task.FromResult(_runs.Any(r =>
            {
                var match = r.Message.GetProperty("run").GetProperty("id").GetString() == runId && r.Message.GetProperty("eventType").GetString() == eventType;
                if (match)
                {
                    found = r;
                }

                return match;
            })), TimeSpan.FromSeconds(60), () => $"no {eventType}; got {string.Join(", ", _runs.Select(r => r.Message.GetProperty("eventType").GetString()))}");
            return found;
        }

        public async Task<JsonElement> WaitForStepAsync(string runId, string status)
        {
            JsonElement found = default;
            await Eventually.WaitAsync(() => Task.FromResult(_steps.Any(s =>
            {
                var match = s.GetProperty("runId").GetString() == runId && s.GetProperty("status").GetString() == status;
                if (match)
                {
                    found = s;
                }

                return match;
            })), TimeSpan.FromSeconds(30), () => $"no step {status}");
            return found;
        }

        public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
    }
}
