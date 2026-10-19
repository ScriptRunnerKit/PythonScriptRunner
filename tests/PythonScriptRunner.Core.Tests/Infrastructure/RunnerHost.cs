using Microsoft.Extensions.Configuration;

namespace PythonScriptRunner.Core.Tests.Infrastructure;

/// <summary>A service provider with the runner configured for tests (isolated work folder, shared venv cache).</summary>
public sealed class RunnerHost : IAsyncDisposable
{
    private RunnerHost(ServiceProvider services, string workRoot)
    {
        Services = services;
        WorkRoot = workRoot;
    }

    public ServiceProvider Services { get; }

    public string WorkRoot { get; }

    public IPythonRunner Runner => Services.GetRequiredService<IPythonRunner>();

    public static RunnerHost Create(
        PythonFixture fixture,
        Action<IPythonScriptRunnerBuilder>? configure = null,
        Action<PythonScriptRunnerOptions>? options = null,
        Action<IServiceCollection>? services = null,
        IDictionary<string, string?>? configuration = null)
    {
        var collection = new ServiceCollection();
        if (configuration is not null)
        {
            collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build());
        }

        var workRoot = Path.Combine(Path.GetTempPath(), "psr-tests", "work", Guid.NewGuid().ToString("N"));
        var builder = collection.AddPythonScriptRunner(o =>
        {
            o.WorkRoot = workRoot;
            o.EnvironmentCacheRoot = fixture.EnvironmentCacheRoot;
            o.UvPath = fixture.UvPath;
            o.PsrunnerRequirement = fixture.WheelPath;
            options?.Invoke(o);
        });
        configure?.Invoke(builder);
        services?.Invoke(collection);
        return new RunnerHost(collection.BuildServiceProvider(validateScopes: true), workRoot);
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

/// <summary>Creates a temporary folder catalog (<c>stages/&lt;key&gt;/stage.json</c> + script).</summary>
public sealed class TempCatalog : IDisposable
{
    public TempCatalog() => Directory.CreateDirectory(Root);

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "psr-tests", "catalogs", Guid.NewGuid().ToString("N"));

    public string AddStage(string folder, string manifest, string script, string? requirements = null, string? requirementsLock = null)
    {
        var path = Path.Combine(Root, "stages", folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "stage.json"), manifest);
        File.WriteAllText(Path.Combine(path, "script.py"), script);
        if (requirements is not null)
        {
            File.WriteAllText(Path.Combine(path, "requirements.txt"), requirements);
        }

        if (requirementsLock is not null)
        {
            File.WriteAllText(Path.Combine(path, "requirements.lock"), requirementsLock);
        }

        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

/// <summary>Collection for timing-sensitive tests (not run in parallel with others).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialCollection
{
    public const string Name = "Serial";
}
