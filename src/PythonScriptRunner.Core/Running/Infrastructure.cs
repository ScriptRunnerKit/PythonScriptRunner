using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PythonScriptRunner.Core.Catalog;

namespace PythonScriptRunner.Core.Running;

/// <summary>Default recorder: nothing is persisted without the EF package.</summary>
internal sealed class NullRunRecorder : IRunRecorder
{
    public ValueTask RunStartedAsync(RunStarted run, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask AttemptFinishedAsync(AttemptFinished attempt, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask RunFinishedAsync(RunFinished run, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Reads secrets from <c>PythonScriptRunner:Secrets:&lt;NAME&gt;</c> (user-secrets, environment variables, Key Vault...).</summary>
internal sealed class ConfigurationSecretProvider(IConfiguration? configuration = null) : ISecretProvider
{
    public const string Section = "PythonScriptRunner:Secrets";

    public ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(configuration?[$"{Section}:{name}"]);
}

/// <summary>Fails fast when the host starts: validates options, loads catalogs and removes leftover folders.</summary>
internal sealed class PythonScriptRunnerStartup(
    IOptions<PythonScriptRunnerOptions> options, ICatalog catalog, WorkspaceJanitor janitor) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = options.Value;
        (catalog as CompositeCatalog)?.EnsureLoaded();
        janitor.EnsureCleaned();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
