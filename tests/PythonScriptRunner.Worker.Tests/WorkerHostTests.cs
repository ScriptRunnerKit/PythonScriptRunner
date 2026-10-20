using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PythonScriptRunner.Worker.Tests;

/// <summary>Spec 009: the standalone WorkerHost (D-107).</summary>
public sealed class WorkerHostTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WorkerHost_MigratesAnswersHealthAndRunsQueuedSteps()
    {
        var database = await postgres.CreateDatabaseAsync();
        var amqp = await broker.CreateVirtualHostAsync();
        var workRoot = Path.Combine(Path.GetTempPath(), "psr-tests", "work", Guid.NewGuid().ToString("N"));
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web => web
            .UseSetting("ConnectionStrings:psr", database)
            .UseSetting("ConnectionStrings:rabbitmq", amqp)
            .UseSetting("PSR_AUTO_MIGRATE", "true")
            .UseSetting("PythonScriptRunner:WorkRoot", workRoot)
            .UseSetting("PythonScriptRunner:UvPath", python.UvPath)
            .UseSetting("PythonScriptRunner:EnvironmentCacheRoot", python.EnvironmentCacheRoot)
            .UseSetting("PythonScriptRunner:PsrunnerRequirement", python.WheelPath)
            .UseSetting("PythonScriptRunner:Worker:MaxConcurrency", "2")
            .UseSetting("Serilog:MinimumLevel:Default", "Warning"));
        using var client = factory.CreateClient();

        (await client.GetAsync("/health/live", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Eventually.WaitAsync(async () => (await client.GetStringAsync("/health/ready", Ct)) == "Healthy", TimeSpan.FromSeconds(30));

        var admin = factory.Services.GetRequiredService<ICatalogAdmin>();
        var draft = await admin.CreateStageAsync(new NewCatalogItem("eco", "eco"), PsrEnvironment.Author, Ct);
        var saved = await admin.SaveStageDraftAsync("eco", draft.Version, draft.Data with
        {
            Script = Scripts.Echo,
            Inputs = [Scripts.Text("texto")],
            Outputs = [Scripts.Text("texto")],
        }, draft.RowVersion, PsrEnvironment.Author, Ct);
        await admin.PublishStageAsync("eco", saved.Version, saved.RowVersion, PsrEnvironment.Author, Ct);
        var handle = await factory.Services.GetRequiredService<IRunQueue>().EnqueueStageAsync("eco", new { texto = "host" }, cancellationToken: Ct);

        var runs = factory.Services.GetRequiredService<IRunManager>();
        RunSnapshot? run = null;
        await Eventually.WaitAsync(async () => (run = await runs.GetAsync(handle.RunId, Ct))?.Status == RunStatus.Succeeded, TimeSpan.FromSeconds(60),
            () => $"run is {run?.Status}");
        run!.Steps.Single().Attempts.Single().WorkerId.ShouldNotBeNullOrEmpty();
    }
}
