using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PythonScriptRunner;
using PythonScriptRunner.Core;
using PythonScriptRunner.Core.Catalog;
using PythonScriptRunner.Core.Diagnostics;
using PythonScriptRunner.Core.Environments;
using PythonScriptRunner.Core.Execution;
using PythonScriptRunner.Core.Running;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers PythonScriptRunner services.</summary>
public static class PythonScriptRunnerServiceCollectionExtensions
{
    /// <summary>Adds the Python runner (<see cref="IPythonRunner"/>) and its default services.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configure">Configures <see cref="PythonScriptRunnerOptions"/>.</param>
    /// <returns>A builder to add catalogs, stages and providers.</returns>
    public static IPythonScriptRunnerBuilder AddPythonScriptRunner(
        this IServiceCollection services, Action<PythonScriptRunnerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLogging();
        services.AddMetrics();
        var options = services.AddOptions<PythonScriptRunnerOptions>().ValidateOnStart();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PythonScriptRunnerOptions>, PythonScriptRunnerOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICatalog, CompositeCatalog>();
        services.TryAddSingleton<ISecretProvider, ConfigurationSecretProvider>();
        services.TryAddSingleton<IRunRecorder, NullRunRecorder>();
        services.TryAddSingleton<IEnvironmentManager, UvEnvironmentManager>();
        services.TryAddSingleton<IPythonExecutor, ProcessPythonExecutor>();
        services.TryAddSingleton<PsrMetrics>();
        services.TryAddSingleton<WorkspaceJanitor>();
        services.TryAddSingleton<IPythonRunner, PythonRunner>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PythonScriptRunnerStartup>());
        return new PythonScriptRunnerBuilder(services);
    }

    private sealed class PythonScriptRunnerBuilder(IServiceCollection services) : IPythonScriptRunnerBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}

/// <summary>Builder returned by <c>AddPythonScriptRunner</c>.</summary>
public interface IPythonScriptRunnerBuilder
{
    /// <summary>The service collection.</summary>
    IServiceCollection Services { get; }
}

/// <summary>Catalog and provider registration.</summary>
public static class PythonScriptRunnerBuilderExtensions
{
    /// <summary>Loads stages from <c>&lt;rootPath&gt;/stages/&lt;folder&gt;/stage.json</c>.</summary>
    /// <param name="builder">Builder.</param>
    /// <param name="rootPath">Catalog folder.</param>
    public static IPythonScriptRunnerBuilder AddFileCatalog(this IPythonScriptRunnerBuilder builder, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        builder.Services.AddSingleton<IStageSource>(new FileStageSource(rootPath));
        return builder;
    }

    /// <summary>Defines a stage in code.</summary>
    /// <param name="builder">Builder.</param>
    /// <param name="key">Stage key (slug).</param>
    /// <param name="configure">Fluent definition.</param>
    /// <exception cref="PsrConfigurationException">The definition is invalid.</exception>
    public static IPythonScriptRunnerBuilder AddStage(this IPythonScriptRunnerBuilder builder, string key, Action<StageBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var stageBuilder = new StageBuilder(key);
        configure(stageBuilder);
        var stage = stageBuilder.Build();
        StageDefinitionValidator.EnsureValid(stage, "code");
        builder.Services.AddSingleton<IStageSource>(new StaticStageSource(stage));
        return builder;
    }

    /// <summary>Replaces the secret provider (default: <c>IConfiguration</c> section <c>PythonScriptRunner:Secrets</c>).</summary>
    /// <typeparam name="TProvider">Provider type.</typeparam>
    /// <param name="builder">Builder.</param>
    public static IPythonScriptRunnerBuilder AddSecretProvider<TProvider>(this IPythonScriptRunnerBuilder builder)
        where TProvider : class, ISecretProvider
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<ISecretProvider, TProvider>());
        return builder;
    }
}
