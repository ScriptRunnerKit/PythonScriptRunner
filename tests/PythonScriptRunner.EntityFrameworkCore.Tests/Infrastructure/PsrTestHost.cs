using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PythonScriptRunner.EntityFrameworkCore.Tests.Infrastructure;

/// <summary>Runner + PostgreSQL persistence on a fresh database (maintenance job off; tests call it explicitly).</summary>
public sealed class PsrTestHost : IAsyncDisposable
{
    private PsrTestHost(ServiceProvider services, string connectionString, string workRoot)
    {
        Services = services;
        ConnectionString = connectionString;
        WorkRoot = workRoot;
    }

    public static Actor Author { get; } = Actor.User("autora@teste");

    public ServiceProvider Services { get; }

    public string ConnectionString { get; }

    public string WorkRoot { get; }

    public IPythonRunner Runner => Services.GetRequiredService<IPythonRunner>();

    public ICatalogAdmin Admin => Services.GetRequiredService<ICatalogAdmin>();

    public IRunManager Runs => Services.GetRequiredService<IRunManager>();

    public IArtifactStore Artifacts => Services.GetRequiredService<IArtifactStore>();

    public PsrMaintenance Maintenance => Services.GetRequiredService<PsrMaintenance>();

    public static async Task<PsrTestHost> CreateAsync(
        PostgresFixture postgres,
        PythonFixture python,
        TimeProvider? time = null,
        Action<PsrEntityFrameworkOptions>? configure = null,
        bool migrate = true,
        Action<IServiceCollection>? services = null)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var collection = new ServiceCollection();
        if (time is not null)
        {
            collection.AddSingleton(time);
        }

        var workRoot = Path.Combine(Path.GetTempPath(), "psr-tests", "work", Guid.NewGuid().ToString("N"));
        collection.AddPythonScriptRunner(o =>
            {
                o.WorkRoot = workRoot;
                o.EnvironmentCacheRoot = python.EnvironmentCacheRoot;
                o.UvPath = python.UvPath;
                o.PsrunnerRequirement = python.WheelPath;
            })
            .AddEntityFrameworkCore(o =>
            {
                o.UseNpgsql(connectionString);
                o.Maintenance.Enabled = false;
                o.LatestVersionCacheDuration = TimeSpan.Zero;
                configure?.Invoke(o);
            });
        services?.Invoke(collection);
        var provider = collection.BuildServiceProvider(validateScopes: true);
        if (migrate)
        {
            await provider.MigratePythonScriptRunnerAsync(TestContext.Current.CancellationToken);
        }

        return new PsrTestHost(provider, connectionString, workRoot);
    }

    public async Task<PsrDbContext> DbAsync() =>
        await Services.GetRequiredService<IDbContextFactory<PsrDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);

    /// <summary>Runs a scalar query on the test database.</summary>
    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Runs a query on the test database and returns the first column of every row.</summary>
    public async Task<List<T>> ColumnAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var values = new List<T>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetFieldValue<T>(0));
        }

        return values;
    }

    /// <summary>Creates, saves and publishes a stage.</summary>
    public async Task<StageVersionDetail> PublishStageAsync(
        string key, string script, IReadOnlyList<FieldDefinition> inputs, IReadOnlyList<FieldDefinition> outputs, string? requirements = null,
        RetryPolicy? retry = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var existing = await Admin.GetStageAsync(key, ct);
        var draft = existing is null
            ? await Admin.CreateStageAsync(new NewCatalogItem(key, key), Author, ct)
            : await Admin.CreateStageDraftFromAsync(key, existing.Versions[0].Version, Author, ct);
        var saved = await Admin.SaveStageDraftAsync(key, draft.Version, draft.Data with
        {
            Script = script,
            Inputs = inputs,
            Outputs = outputs,
            Requirements = requirements,
            Retry = retry ?? RetryPolicy.None,
        }, draft.RowVersion, Author, ct);
        return await Admin.PublishStageAsync(key, saved.Version, saved.RowVersion, Author, ct);
    }

    /// <summary>Creates, saves and publishes a pipeline.</summary>
    public async Task<PipelinePublication> PublishPipelineAsync(string key, PipelineDraftData data)
    {
        var ct = TestContext.Current.CancellationToken;
        var draft = await Admin.CreatePipelineAsync(new NewCatalogItem(key, key), Author, ct);
        var saved = await Admin.SavePipelineDraftAsync(key, draft.Version, data, draft.RowVersion, Author, ct);
        return await Admin.PublishPipelineAsync(key, saved.Version, saved.RowVersion, Author, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
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
    }
}

/// <summary>Scripts shared by the persistence tests.</summary>
public static class TestScripts
{
    public const string Echo = """
        from psrunner import stage


        @stage
        def run(inputs, ctx):
            print("eco:", inputs["texto"])
            return {"texto": inputs["texto"]}
        """;

    public const string WriteFile = """
        from psrunner import stage


        @stage
        def run(inputs, ctx):
            output = ctx.new_file("saida.txt", "text/plain")
            output.path.write_text(inputs["texto"] * 3, encoding="utf-8")
            print("arquivo gerado")
            return {"arquivo": output, "tamanho": len(inputs["texto"]) * 3}
        """;

    public const string ReadFile = """
        from psrunner import stage


        @stage
        def run(inputs, ctx):
            return {"conteudo": inputs["arquivo"].read_text(encoding="utf-8")}
        """;

    /// <summary>Fails the first time (creates the marker file) and succeeds afterwards.</summary>
    public const string FailOnce = """
        import os

        from psrunner import StageError, stage


        @stage
        def run(inputs, ctx):
            marker = inputs["marcador"]
            if not os.path.exists(marker):
                open(marker, "w").close()
                raise StageError("primeira tentativa falha", code="PRIMEIRA")
            return {"texto": inputs["texto"].upper()}
        """;

    public static FieldDefinition Text(string name) => new(name, FieldType.String);

    public static FieldDefinition File(string name) => new(name, FieldType.File);
}
