// Standalone worker of PythonScriptRunner (spec 009): runs queued steps, cron schedules and the maintenance job.
// Configuration comes from environment variables, e.g. ConnectionStrings__psr, ConnectionStrings__rabbitmq,
// PythonScriptRunner__Worker__MaxConcurrency, PythonScriptRunner__Secrets__<NAME> and PSR_AUTO_MIGRATE.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("psr-worker");

var configuration = builder.Configuration;
builder.Services
    .AddPythonScriptRunner(o => configuration.GetSection("PythonScriptRunner").Bind(o))
    .AddEntityFrameworkCore(o =>
    {
        configuration.GetSection("PythonScriptRunner:EntityFrameworkCore").Bind(o);
        o.UseNpgsql(configuration.GetConnectionString("psr")
            ?? throw new InvalidOperationException("Set the PostgreSQL connection string ConnectionStrings__psr."));
    })
    .AddRabbitMq(o =>
    {
        configuration.GetSection("PythonScriptRunner:RabbitMQ").Bind(o);
        o.ConnectionString = configuration.GetConnectionString("rabbitmq")
            ?? throw new InvalidOperationException("Set the RabbitMQ connection string ConnectionStrings__rabbitmq.");
    })
    .AddWorker(o => configuration.GetSection("PythonScriptRunner:Worker").Bind(o));
builder.Services.AddHealthChecks().AddPythonScriptRunnerChecks();

var app = builder.Build();
if (configuration.GetValue<bool>("PSR_AUTO_MIGRATE"))
{
    // EF Core serializes concurrent migrations with a PostgreSQL advisory lock, so replicas can start together (spec 009, AC-8).
    await app.Services.MigratePythonScriptRunnerAsync();
}

app.MapDefaultEndpoints();
await app.RunAsync();

/// <summary>Entry point (public for the integration tests).</summary>
public partial class Program;
