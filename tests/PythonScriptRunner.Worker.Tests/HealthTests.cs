using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;

namespace PythonScriptRunner.Worker.Tests;

/// <summary>Spec 006, AC-16: readiness of a worker.</summary>
public sealed class HealthTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python)
{
    [Fact]
    public async Task Spec006_AC16_Ready_IsHealthyDegradedWithoutBrokerAndUnhealthyWithoutPartitions()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var healthy = await env.StartHostAsync(WithChecks);
        await Eventually.WaitAsync(() => Task.FromResult(healthy.Broker.IsConnected), TimeSpan.FromSeconds(10));
        (await ReadyAsync(healthy)).Status.ShouldBe(HealthStatus.Healthy);

        var withoutBroker = await env.StartHostAsync(s =>
        {
            WithChecks(s);
            s.BrokerConnectionString = "amqp://guest:guest@127.0.0.1:1/";
        });
        var degraded = await ReadyAsync(withoutBroker);
        degraded.Status.ShouldBe(HealthStatus.Degraded);
        degraded.Entries["psr-broker"].Status.ShouldBe(HealthStatus.Degraded);

        // 120 days ahead no partition has been created for "now".
        await using var future = await PsrEnvironment.CreateAsync(postgres, broker, python, new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(120)));
        var stale = await future.StartHostAsync(WithChecks);
        var unhealthy = await ReadyAsync(stale);
        unhealthy.Status.ShouldBe(HealthStatus.Unhealthy);
        unhealthy.Entries["psr-database"].Status.ShouldBe(HealthStatus.Unhealthy);
    }

    private static void WithChecks(HostSettings settings) => settings.Services = services => services.AddHealthChecks().AddPythonScriptRunnerChecks();

    private static Task<HealthReport> ReadyAsync(PsrHost host) =>
        host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(r => r.Tags.Contains("ready"), TestContext.Current.CancellationToken);
}
