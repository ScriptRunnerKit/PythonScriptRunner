using System.IO.Compression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using PythonScriptRunner.EntityFrameworkCore;
using PythonScriptRunner.RabbitMQ;

namespace PythonScriptRunner.Testing;

/// <summary>
/// One isolated deployment for a test: a fresh migrated database and a fresh RabbitMQ virtual host. Hosts (application,
/// workers) started from it share both, like separate processes would.
/// </summary>
public sealed class PsrEnvironment : IAsyncDisposable
{
    private readonly List<PsrHost> _hosts = [];

    private PsrEnvironment(string database, string broker, PythonFixture python, FakeTimeProvider? time)
    {
        Database = database;
        Broker = broker;
        Python = python;
        Time = time;
    }

    public static Actor Author { get; } = Actor.User("autora@teste");

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public string Database { get; }

    public string Broker { get; }

    public PythonFixture Python { get; }

    /// <summary>Simulated time shared by every host (the database clock is then not used).</summary>
    public FakeTimeProvider? Time { get; }

    public string WorkRoot { get; } = Path.Combine(Path.GetTempPath(), "psr-tests", "work", Guid.NewGuid().ToString("N"));

    public static async Task<PsrEnvironment> CreateAsync(
        PostgresFixture postgres, BrokerFixture broker, PythonFixture python, FakeTimeProvider? time = null, bool migrate = true)
    {
        var environment = new PsrEnvironment(await postgres.CreateDatabaseAsync(), await broker.CreateVirtualHostAsync(), python, time);
        if (migrate)
        {
            var services = new ServiceCollection();
            services.AddPythonScriptRunner().AddEntityFrameworkCore(o => o.UseNpgsql(environment.Database));
            await using var provider = services.BuildServiceProvider();
            await provider.MigratePythonScriptRunnerAsync(Ct);
        }

        return environment;
    }

    /// <summary>Starts a host (application and/or worker) on this deployment.</summary>
    public async Task<PsrHost> StartHostAsync(Action<HostSettings>? configure = null)
    {
        var host = CreateHost(configure);
        await host.Host.StartAsync(Ct);
        return host;
    }

    /// <summary>Builds a host without starting it.</summary>
    public PsrHost CreateHost(Action<HostSettings>? configure = null)
    {
        var settings = new HostSettings();
        configure?.Invoke(settings);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = "Test" });
        builder.Configuration.AddInMemoryCollection(settings.Configuration);
        if (Environment.GetEnvironmentVariable("PSR_TEST_LOG") is { Length: > 0 } logFile)
        {
            builder.Logging.AddFilter(level => level >= LogLevel.Debug);
            builder.Logging.AddProvider(new TestFileLoggerProvider(logFile, settings.InstanceId));
        }
        else
        {
            builder.Logging.AddFilter(level => level >= LogLevel.Warning);
        }
        if (Time is not null)
        {
            builder.Services.AddSingleton<TimeProvider>(Time);
        }

        builder.Services.AddSingleton<EventRecorder>();
        var psr = builder.Services.AddPythonScriptRunner(o =>
            {
                o.WorkRoot = Path.Combine(WorkRoot, settings.InstanceId);
                o.EnvironmentCacheRoot = Python.EnvironmentCacheRoot;
                o.UvPath = Python.UvPath;
                o.PsrunnerRequirement = Python.WheelPath;
            })
            .AddEntityFrameworkCore(o =>
            {
                o.UseNpgsql(Database);
                o.Maintenance.Enabled = false;
                o.LatestVersionCacheDuration = TimeSpan.Zero;
                o.UseDatabaseClock = Time is null;
            });
        if (settings.Broker)
        {
            psr.AddRabbitMq(o =>
            {
                o.ConnectionString = settings.BrokerConnectionString ?? Broker;
                o.InstanceId = settings.InstanceId;
                o.DeclareTopology = settings.DeclareTopology;
                o.RunOutboxRelay = settings.Relay;
                o.EnablePublicCommands = settings.PublicCommands;
                o.RelayInterval = TimeSpan.FromMilliseconds(100);
                o.StartupConnectTimeout = TimeSpan.FromSeconds(3);
            });
            if (settings.RecordEvents)
            {
                psr.AddRunEventHandler<EventRecorder>(s => s.PerInstance("tests").On(settings.EventPatterns));
            }
        }

        if (settings.Worker)
        {
            psr.AddWorker(o =>
            {
                o.WorkerId = settings.InstanceId;
                o.MaxConcurrency = settings.MaxConcurrency;
                o.LeaseDuration = settings.LeaseDuration;
                o.LeaseRenewInterval = settings.LeaseRenewInterval;
                o.ReaperInterval = settings.ReaperInterval;
                o.ShutdownGracePeriod = settings.ShutdownGracePeriod;
                o.Scheduler.Enabled = settings.Scheduler;
                o.Scheduler.PollInterval = settings.SchedulerPollInterval;
            });
        }

        settings.Builder?.Invoke(psr);
        settings.Services?.Invoke(builder.Services);
        var host = new PsrHost(builder.Build(), settings.InstanceId);
        _hosts.Add(host);
        return host;
    }

    /// <summary>Creates, saves and publishes a stage.</summary>
    public static async Task<StageVersionDetail> PublishStageAsync(
        PsrHost host, string key, string script, IReadOnlyList<FieldDefinition> inputs, IReadOnlyList<FieldDefinition> outputs,
        RetryPolicy? retry = null, bool idempotent = false, string? requirements = null)
    {
        var existing = await host.Admin.GetStageAsync(key, Ct);
        var draft = existing is null
            ? await host.Admin.CreateStageAsync(new NewCatalogItem(key, key), Author, Ct)
            : await host.Admin.CreateStageDraftFromAsync(key, existing.Versions[0].Version, Author, Ct);
        var saved = await host.Admin.SaveStageDraftAsync(key, draft.Version, draft.Data with
        {
            Script = script,
            Inputs = inputs,
            Outputs = outputs,
            Requirements = requirements,
            Retry = retry ?? RetryPolicy.None,
            Idempotent = idempotent,
        }, draft.RowVersion, Author, Ct);
        return await host.Admin.PublishStageAsync(key, saved.Version, saved.RowVersion, Author, Ct);
    }

    /// <summary>Creates (or drafts a new version of) and publishes a pipeline.</summary>
    public static async Task<PipelinePublication> PublishPipelineAsync(PsrHost host, string key, PipelineDraftData data)
    {
        var existing = await host.Admin.GetPipelineAsync(key, Ct);
        var draft = existing is null
            ? await host.Admin.CreatePipelineAsync(new NewCatalogItem(key, key), Author, Ct)
            : await host.Admin.CreatePipelineDraftFromAsync(key, existing.Versions[0].Version, Author, Ct);
        var saved = await host.Admin.SavePipelineDraftAsync(key, draft.Version, data, draft.RowVersion, Author, Ct);
        return await host.Admin.PublishPipelineAsync(key, saved.Version, saved.RowVersion, Author, Ct);
    }

    /// <summary>
    /// Imports a fixture folder (<c>tests/fixtures/&lt;name&gt;</c>) and publishes its stages and pipelines.
    /// <paramref name="transform"/> may rewrite file contents (e.g. point a constant at a test container).
    /// </summary>
    public async Task ImportFixtureAsync(PsrHost host, string name, Func<string, string, string>? transform = null)
    {
        var root = Path.Combine(Python.RepositoryRoot, "tests", "fixtures", name);
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains("__pycache__", StringComparison.Ordinal)))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var content = await File.ReadAllTextAsync(file, Ct);
                await using var writer = new StreamWriter(archive.CreateEntry(relative).Open());
                await writer.WriteAsync(transform is null ? content : transform(relative, content));
            }
        }

        zip.Position = 0;
        var report = await host.Admin.ImportAsync(zip, Author, Ct);
        foreach (var stage in report.Items.Where(i => i.Kind == "Stage"))
        {
            var draft = (await host.Admin.GetStageDraftAsync(stage.Key, Ct))!;
            await host.Admin.PublishStageAsync(stage.Key, draft.Version, draft.RowVersion, Author, Ct);
        }

        foreach (var pipeline in report.Items.Where(i => i.Kind == "Pipeline"))
        {
            var draft = (await host.Admin.GetPipelineDraftAsync(pipeline.Key, Ct))!;
            await host.Admin.PublishPipelineAsync(pipeline.Key, draft.Version, draft.RowVersion, Author, Ct);
        }
    }

    /// <summary>Runs a scalar query on the database.</summary>
    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(Database);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>Runs a query and returns the first column of every row.</summary>
    public async Task<List<T>> ColumnAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(Database);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var values = new List<T>();
        while (await reader.ReadAsync(Ct))
        {
            values.Add(reader.GetFieldValue<T>(0));
        }

        return values;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in Enumerable.Reverse(_hosts))
        {
            await host.DisposeAsync();
        }

        try
        {
            if (Directory.Exists(WorkRoot))
            {
                Directory.Delete(WorkRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}

/// <summary>What a test host runs.</summary>
public sealed class HostSettings
{
    public string InstanceId { get; set; } = $"test-{Guid.NewGuid().ToString("N")[..8]}";

    public bool Broker { get; set; } = true;

    public string? BrokerConnectionString { get; set; }

    public bool Worker { get; set; } = true;

    public bool Relay { get; set; } = true;

    public bool PublicCommands { get; set; } = true;

    public bool DeclareTopology { get; set; } = true;

    public bool RecordEvents { get; set; } = true;

    public string[] EventPatterns { get; set; } = ["#"];

    public int MaxConcurrency { get; set; } = 4;

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan LeaseRenewInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan ReaperInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan ShutdownGracePeriod { get; set; } = TimeSpan.FromSeconds(30);

    public bool Scheduler { get; set; }

    public TimeSpan SchedulerPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public Dictionary<string, string?> Configuration { get; } = [];

    public Action<IPythonScriptRunnerBuilder>? Builder { get; set; }

    public Action<IServiceCollection>? Services { get; set; }
}

/// <summary>A started host and its services.</summary>
public sealed class PsrHost(IHost host, string instanceId) : IAsyncDisposable
{
    private bool _disposed;

    public IHost Host { get; } = host;

    public string InstanceId { get; } = instanceId;

    public IServiceProvider Services => Host.Services;

    public IRunQueue Queue => Services.GetRequiredService<IRunQueue>();

    public IPythonRunner Runner => Services.GetRequiredService<IPythonRunner>();

    public ICatalogAdmin Admin => Services.GetRequiredService<ICatalogAdmin>();

    public IRunManager Runs => Services.GetRequiredService<IRunManager>();

    public IArtifactStore Artifacts => Services.GetRequiredService<IArtifactStore>();

    public AsyncRunLifecycle Lifecycle => Services.GetRequiredService<AsyncRunLifecycle>();

    public PsrBroker Broker => Services.GetRequiredService<PsrBroker>();

    public EventRecorder Events => Services.GetRequiredService<EventRecorder>();

    /// <summary>Waits until the run satisfies <paramref name="condition"/> (default: a final status).</summary>
    public async Task<RunSnapshot> WaitForRunAsync(RunId runId, Func<RunSnapshot, bool>? condition = null, TimeSpan? timeout = null)
    {
        condition ??= r => r.Status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Cancelled;
        RunSnapshot? last = null;
        await Eventually.WaitAsync(async () =>
        {
            last = await Runs.GetAsync(runId, PsrEnvironment.Ct);
            return last is not null && condition(last);
        }, timeout ?? TimeSpan.FromSeconds(60), () => $"run {runId} is {last?.Status}");
        return last!;
    }

    /// <summary>Stops the host gracefully.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_disposed)
        {
            await Host.StopAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await Host.StopAsync(CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }

        Host.Dispose();
    }
}
