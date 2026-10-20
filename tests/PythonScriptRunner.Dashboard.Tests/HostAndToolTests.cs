using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using PythonScriptRunner.Dashboard.Tests.Infrastructure;
using PythonScriptRunner.Tool;

namespace PythonScriptRunner.Dashboard.Tests;

/// <summary>Spec 009: the standalone DashboardHost (AC-9, AC-10; D-125) and <c>psr hash-password</c> (AC-11; D-124).</summary>
public sealed class HostAndToolTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spec009_AC09_DashboardHost_FixedLoginUsersFromEnvironment()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using var factory = Host(env, web => web
            .UseSetting("PythonScriptRunner:Dashboard:Login:Users:0:Name", "ana")
            .UseSetting("PythonScriptRunner:Dashboard:Login:Users:0:Role", "Author")
            .UseSetting("PythonScriptRunner:Dashboard:Login:Users:0:PasswordHash", TestUsers.Hash));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-PSR-Request", "1");

        (await client.GetAsync("/psr/api/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/psr/api/auth/login", new { name = "ana", password = TestUsers.Password }, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        var me = await (await client.GetAsync("/psr/api/me", Ct)).JsonAsync();
        me["role"]!.GetValue<string>().ShouldBe("Author");
        me["subject"]!.GetValue<string>().ShouldBe("login:ana");
        (await client.GetAsync("/health/live", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await client.GetAsync("/psr/", Ct)).Content.ReadAsStringAsync(Ct)).ShouldContain("<base href=\"/psr/\">");
    }

    [Fact]
    public async Task Spec009_AC09_DashboardHost_WithoutUsersAndWithoutOidc_FailsWithAClearMessage()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using var factory = Host(env, _ => { });

        var error = Should.Throw<Exception>(() => factory.CreateClient());

        var messages = string.Join(" | ", Unwrap(error).Select(e => e.Message));
        messages.ShouldContain("PythonScriptRunner__Dashboard__Login__Users__0__Name");
        messages.ShouldContain("Oidc__Authority");
    }

    [Fact]
    public async Task Spec009_AC10_DashboardHost_WithOidc_ChallengesThePanelAndTheApiAnswers401()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using var factory = Host(env, web => web
            .UseSetting("Oidc:Authority", "https://idp.exemplo.com")
            .UseSetting("Oidc:ClientId", "psr-dashboard")
            .UseSetting("Oidc:ClientSecret", "segredo")
            .ConfigureTestServices(services => services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
                o.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = "https://idp.exemplo.com",
                    AuthorizationEndpoint = "https://idp.exemplo.com/authorize",
                    EndSessionEndpoint = "https://idp.exemplo.com/logout",
                })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var panel = await client.GetAsync("/psr/execucoes", Ct);
        var api = await client.GetAsync("/psr/api/stages", Ct);
        var auth = await (await client.GetAsync("/psr/api/auth", Ct)).JsonAsync();
        var login = await client.GetAsync("/account/login?returnUrl=%2Fpsr%2F", Ct);

        panel.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        panel.Headers.Location!.ToString().ShouldStartWith("https://idp.exemplo.com/authorize?");
        panel.Headers.Location.Query.ShouldContain("client_id=psr-dashboard");
        api.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await api.JsonAsync())["code"]!.GetValue<string>().ShouldBe("UNAUTHENTICATED");
        auth["mode"]!.GetValue<string>().ShouldBe("Host");
        auth["loginUrl"]!.GetValue<string>().ShouldBe("/account/login?returnUrl=%2Fpsr%2F");
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        login.Headers.Location!.ToString().ShouldStartWith("https://idp.exemplo.com/authorize?");
    }

    [Fact]
    public async Task Spec009_AC11_HashPassword_FromStdin_IsAcceptedByTheFixedLoginAndNeverPrinted()
    {
        const string password = "Uma senha longa 123!";
        var (code, output, error) = RunTool(["hash-password", "--stdin"], password + Environment.NewLine);

        code.ShouldBe(0, error);
        var hash = output.Trim();
        DashboardPasswords.IsHash(hash).ShouldBeTrue();
        output.ShouldNotContain(password);
        error.ShouldNotContain(password);

        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using var app = await DashboardApp.StartAsync(env, s =>
        {
            s.Configuration.Clear();
            foreach (var (key, value) in TestUsers.Configuration(("carla", DashboardRole.Admin, hash)))
            {
                s.Configuration[key] = value;
            }
        });
        using var client = await app.LoginAsync("carla", password);
        (await (await client.GetAsync("/psr/api/me", Ct)).JsonAsync())["role"]!.GetValue<string>().ShouldBe("Admin");
    }

    [Fact]
    public void Spec009_AC11_HashPassword_TypedTwiceWithoutEcho()
    {
        var typed = new Queue<string>(["segredo-digitado", "segredo-digitado"]);
        var (code, output, error) = RunTool(["hash-password"], string.Empty, typed.Dequeue, redirected: false);

        code.ShouldBe(0, error);
        DashboardPasswords.Verify(output.Trim(), "segredo-digitado").ShouldBeTrue();
        (output + error).ShouldNotContain("segredo-digitado");
        error.ShouldContain("Password:");

        var mismatch = new Queue<string>(["um", "outro"]);
        RunTool(["hash-password"], string.Empty, mismatch.Dequeue, redirected: false).Code.ShouldBe(1);
        RunTool(["hash-password"], "senha\n", redirected: true).Code.ShouldBe(1, "a redirected input needs --stdin");
        RunTool(["hash-password", "minha-senha"], string.Empty).Code.ShouldBe(1, "the password is never an argument");
        RunTool(["hash-password", "--stdin"], "\n").Code.ShouldBe(1, "empty password");
        RunTool([], string.Empty).Code.ShouldBe(1);
    }

    private static (int Code, string Output, string Error) RunTool(string[] args, string input, Func<string>? readSecret = null, bool redirected = true)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = PsrTool.Run(args, new ToolConsole(new StringReader(input), output, error, redirected,
            readSecret ?? (() => throw new InvalidOperationException("no terminal"))));
        return (code, output.ToString(), error.ToString());
    }

    private WebApplicationFactory<Program> Host(PsrEnvironment env, Action<IWebHostBuilder> configure) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("ConnectionStrings:psr", env.Database)
                .UseSetting("ConnectionStrings:rabbitmq", env.Broker)
                .UseSetting("PythonScriptRunner:WorkRoot", Path.Combine(env.WorkRoot, "host"))
                .UseSetting("PythonScriptRunner:UvPath", python.UvPath)
                .UseSetting("PythonScriptRunner:EnvironmentCacheRoot", python.EnvironmentCacheRoot)
                .UseSetting("PythonScriptRunner:PsrunnerRequirement", python.WheelPath)
                .UseSetting("Serilog:MinimumLevel:Default", "Warning");
            configure(web);
        });

    private static IEnumerable<Exception> Unwrap(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Unwrap))
                {
                    yield return inner;
                }
            }
        }
    }
}
