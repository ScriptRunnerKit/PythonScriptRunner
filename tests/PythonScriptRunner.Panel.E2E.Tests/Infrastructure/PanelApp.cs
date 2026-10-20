using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using PythonScriptRunner.Dashboard;

namespace PythonScriptRunner.Panel.E2E.Tests.Infrastructure;

/// <summary>Users of the fixed login, one per role, all with <see cref="Password"/>.</summary>
public static class PanelUsers
{
    public const string Password = "senha-de-teste";

    public const string Reader = "leitor";

    public const string Executor = "executor";

    public const string Author = "autor";

    public const string Admin = "admin";

    private static readonly Lazy<string> Hash = new(() => DashboardPasswords.Hash(Password));

    /// <summary>Configuration keys of the users (<c>PythonScriptRunner:Dashboard:Login:Users</c>).</summary>
    public static IEnumerable<KeyValuePair<string, string?>> Configuration()
    {
        (string Name, DashboardRole Role)[] users =
            [(Reader, DashboardRole.Reader), (Executor, DashboardRole.Executor), (Author, DashboardRole.Author), (Admin, DashboardRole.Admin)];
        for (var i = 0; i < users.Length; i++)
        {
            yield return new($"PythonScriptRunner:Dashboard:Login:Users:{i}:Name", users[i].Name);
            yield return new($"PythonScriptRunner:Dashboard:Login:Users:{i}:Role", users[i].Role.ToString());
            yield return new($"PythonScriptRunner:Dashboard:Login:Users:{i}:PasswordHash", Hash.Value);
        }
    }
}

/// <summary>What a panel test deploys.</summary>
public sealed class PanelAppSettings
{
    /// <summary>Runs a worker next to the dashboard (false: runs stay queued).</summary>
    public bool Worker { get; set; } = true;
}

/// <summary>
/// One deployment for a test (D-156): a fresh database and RabbitMQ virtual host, the standalone DashboardHost on Kestrel
/// with the 4 fixed login users, and a worker host (also used to prepare the catalog through <see cref="Host"/>).
/// </summary>
public sealed class PanelApp : IAsyncDisposable
{
    private readonly DashboardHostFactory _factory;

    private PanelApp(PsrEnvironment environment, PsrHost host, DashboardHostFactory factory, Uri url)
    {
        Environment = environment;
        Host = host;
        _factory = factory;
        Url = url;
    }

    public PsrEnvironment Environment { get; }

    /// <summary>Host with the catalog administration, the queue and (unless disabled) the worker.</summary>
    public PsrHost Host { get; }

    /// <summary>Address of the panel (<c>http://127.0.0.1:port/psr/</c>).</summary>
    public Uri Url { get; }

    public static async Task<PanelApp> StartAsync(
        PostgresFixture postgres, BrokerFixture broker, PythonFixture python, Action<PanelAppSettings>? configure = null)
    {
        var settings = new PanelAppSettings();
        configure?.Invoke(settings);
        var environment = await PsrEnvironment.CreateAsync(postgres, broker, python);
        DashboardHostFactory? factory = null;
        try
        {
            var host = await environment.StartHostAsync(s => s.Worker = settings.Worker);
            factory = new DashboardHostFactory(web =>
            {
                web.UseSetting("ConnectionStrings:psr", environment.Database)
                    .UseSetting("ConnectionStrings:rabbitmq", environment.Broker)
                    .UseSetting("PythonScriptRunner:WorkRoot", Path.Combine(environment.WorkRoot, "dashboard"))
                    .UseSetting("PythonScriptRunner:UvPath", python.UvPath)
                    .UseSetting("PythonScriptRunner:EnvironmentCacheRoot", python.EnvironmentCacheRoot)
                    .UseSetting("PythonScriptRunner:PsrunnerRequirement", python.WheelPath)
                    .UseSetting("PythonScriptRunner:RabbitMQ:RelayInterval", "00:00:00.100")
                    .UseSetting("Serilog:MinimumLevel:Default", "Warning");
                foreach (var (key, value) in PanelUsers.Configuration())
                {
                    web.UseSetting(key, value);
                }
            });
            factory.UseKestrel(0);
            factory.StartServer();
            var address = new Uri(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
            var url = new Uri($"http://127.0.0.1:{address.Port}/psr/");
            await EnsurePanelAsync(url);
            return new PanelApp(environment, host, factory, url);
        }
        catch
        {
            if (factory is not null)
            {
                await factory.DisposeAsync();
            }

            await environment.DisposeAsync();
            throw;
        }
    }

    /// <summary>Absolute address of a panel path (e.g. <c>etapas/eco</c>) or API path (e.g. <c>api/me</c>).</summary>
    public string UrlOf(string path) => new Uri(Url, path).ToString();

    /// <summary>Imports and publishes a fixture of <c>tests/fixtures</c> (stages and pipelines).</summary>
    public Task ImportFixtureAsync(string name) => Environment.ImportFixtureAsync(Host, name);

    /// <summary>Creates a stage and saves its first draft (not published).</summary>
    public async Task<StageDraft> SaveStageDraftAsync(string key, string script, IReadOnlyList<FieldDefinition> inputs, IReadOnlyList<FieldDefinition> outputs)
    {
        var draft = await Host.Admin.CreateStageAsync(new NewCatalogItem(key, key), PsrEnvironment.Author, PsrEnvironment.Ct);
        return await Host.Admin.SaveStageDraftAsync(
            key, draft.Version, draft.Data with { Script = script, Inputs = inputs, Outputs = outputs }, draft.RowVersion, PsrEnvironment.Author, PsrEnvironment.Ct);
    }

    /// <summary>Creates a pipeline and saves its first draft (not published).</summary>
    public async Task<PipelineDraft> SavePipelineDraftAsync(string key, PipelineDraftData data)
    {
        var draft = await Host.Admin.CreatePipelineAsync(new NewCatalogItem(key, key), PsrEnvironment.Author, PsrEnvironment.Ct);
        return await Host.Admin.SavePipelineDraftAsync(key, draft.Version, data, draft.RowVersion, PsrEnvironment.Author, PsrEnvironment.Ct);
    }

    /// <summary>Creates (or drafts a new version of) and publishes a stage.</summary>
    public Task<StageVersionDetail> PublishStageAsync(string key, string script, IReadOnlyList<FieldDefinition> inputs, IReadOnlyList<FieldDefinition> outputs) =>
        PsrEnvironment.PublishStageAsync(Host, key, script, inputs, outputs);

    /// <summary>Creates (or drafts a new version of) and publishes a pipeline.</summary>
    public Task<PipelinePublication> PublishPipelineAsync(string key, PipelineDraftData data) => PsrEnvironment.PublishPipelineAsync(Host, key, data);

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await Environment.DisposeAsync();
    }

    /// <summary>The tests drive the real panel: the placeholder page of non-Release builds would only time out.</summary>
    private static async Task EnsurePanelAsync(Uri url)
    {
        using var http = new HttpClient();
        var html = await http.GetStringAsync(url, PsrEnvironment.Ct);
        if (!html.Contains("id=\"app\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Dashboard embeds the placeholder page, not the panel: run the E2E tests from a Release build "
                + "(dotnet test -c Release) or with -p:PsrBuildPanel=true (needs Node 24).");
        }
    }

    private sealed class DashboardHostFactory(Action<IWebHostBuilder> configure) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => configure(builder);
    }
}
