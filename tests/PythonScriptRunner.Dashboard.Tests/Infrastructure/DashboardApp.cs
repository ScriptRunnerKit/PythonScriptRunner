using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PythonScriptRunner.Dashboard.Tests.Infrastructure;

/// <summary>What a dashboard test application runs.</summary>
public sealed class DashboardAppSettings
{
    public string Prefix { get; set; } = "/psr";

    public string Environment { get; set; } = "Production";

    public string InstanceId { get; set; } = $"dash-{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>Access mode; default: fixed login with <see cref="TestUsers"/> from configuration.</summary>
    public Action<DashboardOptions> Dashboard { get; set; } = o => o.UseFixedLogin(l => l.BindConfiguration());

    public Dictionary<string, string?> Configuration { get; } = new(TestUsers.Configuration());

    /// <summary>Configuration that a test can change while the application runs (D-128).</summary>
    public ReloadableConfigurationSource? Reloadable { get; set; }

    public bool Map { get; set; } = true;

    public Action<IServiceCollection>? Services { get; set; }

    /// <summary>Extra PythonScriptRunner registrations (e.g. event handlers).</summary>
    public Action<IPythonScriptRunnerBuilder>? Builder { get; set; }

    /// <summary>Middleware added before the dashboard is mapped (e.g. host authentication).</summary>
    public Action<WebApplication>? Pipeline { get; set; }
}

/// <summary>A web application with the dashboard on a test server, connected to a <see cref="PsrEnvironment"/>.</summary>
public sealed class DashboardApp : IAsyncDisposable
{
    private DashboardApp(WebApplication app, DashboardAppSettings settings)
    {
        App = app;
        Settings = settings;
    }

    public WebApplication App { get; }

    public DashboardAppSettings Settings { get; }

    public IServiceProvider Services => App.Services;

    public TestServer Server => App.GetTestServer();

    public string Api => $"{Settings.Prefix}/api";

    public ICatalogAdmin Admin => Services.GetRequiredService<ICatalogAdmin>();

    public IRunManager Runs => Services.GetRequiredService<IRunManager>();

    /// <summary>Builds and starts the application.</summary>
    public static async Task<DashboardApp> StartAsync(PsrEnvironment env, Action<DashboardAppSettings>? configure = null)
    {
        var app = Build(env, configure, out var settings);
        try
        {
            await app.StartAsync(PsrEnvironment.Ct);
        }
        catch
        {
            // Stops what started before the failure (consumers, relay).
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
            throw;
        }

        return new DashboardApp(app, settings);
    }

    /// <summary>Builds the application without starting it (startup failures are thrown by <c>StartAsync</c>).</summary>
    public static WebApplication Build(PsrEnvironment env, Action<DashboardAppSettings>? configure, out DashboardAppSettings settings)
    {
        settings = new DashboardAppSettings();
        configure?.Invoke(settings);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = settings.Environment,
            ApplicationName = typeof(DashboardApp).Assembly.GetName().Name,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings.Configuration);
        if (settings.Reloadable is not null)
        {
            ((IConfigurationBuilder)builder.Configuration).Add(settings.Reloadable);
        }

        builder.Logging.ClearProviders();
        if (System.Environment.GetEnvironmentVariable("PSR_TEST_LOG") is { Length: > 0 } logFile)
        {
            builder.Logging.AddFilter(level => level >= LogLevel.Debug);
            builder.Logging.AddProvider(new TestFileLoggerProvider(logFile, settings.InstanceId));
        }

        if (env.Time is not null)
        {
            builder.Services.AddSingleton<TimeProvider>(env.Time);
        }

        builder.Services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>();
        var instance = settings.InstanceId;
        var dashboard = settings.Dashboard;
        var psr = builder.Services
            .AddPythonScriptRunner(o =>
            {
                o.WorkRoot = Path.Combine(env.WorkRoot, instance);
                o.EnvironmentCacheRoot = env.Python.EnvironmentCacheRoot;
                o.UvPath = env.Python.UvPath;
                o.PsrunnerRequirement = env.Python.WheelPath;
            })
            .AddEntityFrameworkCore(o =>
            {
                o.UseNpgsql(env.Database);
                o.Maintenance.Enabled = false;
                o.LatestVersionCacheDuration = TimeSpan.Zero;
                o.UseDatabaseClock = env.Time is null;
            })
            .AddRabbitMq(o =>
            {
                o.ConnectionString = env.Broker;
                o.InstanceId = instance;
                o.RelayInterval = TimeSpan.FromMilliseconds(100);
                o.StartupConnectTimeout = TimeSpan.FromSeconds(3);
            })
            .AddDashboard(dashboard);
        settings.Builder?.Invoke(psr);
        settings.Services?.Invoke(builder.Services);
        var app = builder.Build();
        settings.Pipeline?.Invoke(app);
        if (settings.Map)
        {
            app.MapPythonScriptRunnerDashboard(settings.Prefix);
        }

        return app;
    }

    /// <summary>A client with its own cookies; mutating requests carry <c>X-PSR-Request: 1</c> unless <paramref name="csrf"/> is false.</summary>
    public DashboardClient CreateClient(string? ip = null, bool csrf = true)
    {
        var cookies = new CookieContainer();
        var client = new DashboardClient(new CookieContainerHandler(cookies) { InnerHandler = Server.CreateHandler() }, cookies)
        {
            BaseAddress = Server.BaseAddress,
        };
        if (csrf)
        {
            client.DefaultRequestHeaders.Add("X-PSR-Request", "1");
        }

        if (ip is not null)
        {
            client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.Header, ip);
        }

        return client;
    }

    /// <summary>A client signed in with the fixed login.</summary>
    public async Task<DashboardClient> LoginAsync(string name, string password = TestUsers.Password)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync($"{Api}/auth/login", new { name, password }, PsrEnvironment.Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(PsrEnvironment.Ct));
        return client;
    }

    /// <summary>Creates, saves and publishes a stage through the library.</summary>
    public async Task<StageVersionDetail> PublishStageAsync(
        string key, string script, IReadOnlyList<FieldDefinition> inputs, IReadOnlyList<FieldDefinition> outputs, IReadOnlyList<string>? secrets = null)
    {
        var existing = await Admin.GetStageAsync(key, PsrEnvironment.Ct);
        var draft = existing is null
            ? await Admin.CreateStageAsync(new NewCatalogItem(key, key), PsrEnvironment.Author, PsrEnvironment.Ct)
            : await Admin.CreateStageDraftFromAsync(key, existing.Versions[0].Version, PsrEnvironment.Author, PsrEnvironment.Ct);
        var saved = await Admin.SaveStageDraftAsync(key, draft.Version, draft.Data with
        {
            Script = script,
            Inputs = inputs,
            Outputs = outputs,
            RequiredSecrets = secrets ?? [],
        }, draft.RowVersion, PsrEnvironment.Author, PsrEnvironment.Ct);
        return await Admin.PublishStageAsync(key, saved.Version, saved.RowVersion, PsrEnvironment.Author, PsrEnvironment.Ct);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await App.StopAsync(CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }

        await App.DisposeAsync();
    }
}

/// <summary>A client of the test server with its cookies.</summary>
public sealed class DashboardClient(HttpMessageHandler handler, CookieContainer cookies) : HttpClient(handler)
{
    public CookieContainer Cookies { get; } = cookies;

    /// <summary>The cookies sent to <paramref name="path"/>, as a <c>Cookie</c> header.</summary>
    public string CookieHeader(string path) => Cookies.GetCookieHeader(new Uri(BaseAddress!, path));
}

/// <summary>Fixed login users of the tests (one per role) and their configuration.</summary>
public static class TestUsers
{
    public const string Password = "senha-de-teste";

    private static readonly Lazy<string> SharedHash = new(() => DashboardPasswords.Hash(Password));

    public static string Hash => SharedHash.Value;

    public static IReadOnlyList<(string Name, DashboardRole Role)> All { get; } =
        [("leitor", DashboardRole.Reader), ("executor", DashboardRole.Executor), ("autor", DashboardRole.Author), ("admin", DashboardRole.Admin)];

    public static string NameOf(DashboardRole role) => All.Single(u => u.Role == role).Name;

    /// <summary>Configuration keys of the users in <c>PythonScriptRunner:Dashboard:Login</c>.</summary>
    public static Dictionary<string, string?> Configuration(params (string Name, DashboardRole Role, string? Hash)[] users)
    {
        var list = users.Length > 0 ? users : [.. All.Select(u => (u.Name, u.Role, (string?)null))];
        var configuration = new Dictionary<string, string?>();
        for (var i = 0; i < list.Length; i++)
        {
            configuration[$"PythonScriptRunner:Dashboard:Login:Users:{i}:Name"] = list[i].Name;
            configuration[$"PythonScriptRunner:Dashboard:Login:Users:{i}:Role"] = list[i].Role.ToString();
            configuration[$"PythonScriptRunner:Dashboard:Login:Users:{i}:PasswordHash"] = list[i].Hash ?? Hash;
        }

        return configuration;
    }
}

/// <summary>Sets the client address of each request (TestServer has none): <c>X-Test-Ip</c>, default 203.0.113.10.</summary>
public sealed class RemoteIpStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-Ip";

    public static readonly IPAddress DefaultAddress = IPAddress.Parse("203.0.113.10");

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = context.Request.Headers.TryGetValue(Header, out var ip) ? IPAddress.Parse(ip.ToString()) : DefaultAddress;
            return nextMiddleware(context);
        });
        next(app);
    };
}

/// <summary>Host authentication of the tests: <c>X-Test-User: sub|name|role1,role2</c>; the challenge redirects like a real host.</summary>
public sealed class HeaderAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestHost";
    public const string Header = "X-Test-User";
    public const string ChallengeLocation = "/host/login";

    public static void Register(DashboardAppSettings settings, string? loginUrl = "/host/login?volta={returnUrl}")
    {
        settings.Services += services => services.AddAuthentication(SchemeName).AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(SchemeName, null);
        settings.Dashboard = o => o.UseHostAuthentication(r =>
        {
            r.Reader = p => p.RequireClaim("role", "psr-leitor");
            r.Executor = p => p.RequireClaim("role", "psr-executor");
            r.Author = p => p.RequireClaim("role", "psr-autor");
            r.Admin = p => p.RequireClaim("role", "psr-admin");
            r.LoginUrl = loginUrl;
            r.LogoutUrl = "/host/logout";
        });
    }

    public static string RoleClaim(DashboardRole role) => role switch
    {
        DashboardRole.Reader => "psr-leitor",
        DashboardRole.Executor => "psr-executor",
        DashboardRole.Author => "psr-autor",
        _ => "psr-admin",
    };

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var value) || value.ToString().Split('|') is not [var sub, var name, var roles])
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        List<Claim> claims = [new("sub", sub), new(ClaimTypes.Name, name), .. roles.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim("role", r))];
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Redirect(ChallengeLocation);
        return Task.CompletedTask;
    }
}

/// <summary>Custom filter of the tests: <c>X-Test-Filter: name|Role</c>; no header denies.</summary>
public sealed class HeaderFilter : IDashboardAuthorizationFilter
{
    public const string Header = "X-Test-Filter";

    public ValueTask<DashboardUser?> AuthorizeAsync(HttpContext context) =>
        ValueTask.FromResult(context.Request.Headers.TryGetValue(Header, out var value) && value.ToString().Split('|') is [var name, var role]
            ? new DashboardUser(name, Enum.Parse<DashboardRole>(role))
            : null);
}

/// <summary>Configuration replaced by a test while the application runs.</summary>
public sealed class ReloadableConfigurationSource : IConfigurationSource
{
    public ReloadableConfigurationProvider Provider { get; } = new();

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}

public sealed class ReloadableConfigurationProvider : ConfigurationProvider
{
    public void Replace(IDictionary<string, string?> values)
    {
        Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        OnReload();
    }
}

/// <summary>Request helpers.</summary>
public static class Api
{
    public static async Task<JsonNode> JsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(PsrEnvironment.Ct);
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty body.");
    }

    public static async Task<HttpResponseMessage> SendJsonAsync(this HttpClient client, HttpMethod method, string url, object? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (ifMatch is not null)
        {
            request.Headers.IfMatch.Add(new EntityTagHeaderValue(ifMatch));
        }

        return await client.SendAsync(request, PsrEnvironment.Ct);
    }

    public static string ETag(this HttpResponseMessage response) => response.Headers.ETag?.Tag ?? throw new InvalidOperationException("No ETag.");
}
