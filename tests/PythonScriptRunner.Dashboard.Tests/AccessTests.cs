using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Time.Testing;
using PythonScriptRunner.Dashboard.Tests.Infrastructure;

namespace PythonScriptRunner.Dashboard.Tests;

/// <summary>Spec 007: access modes, configuration and the fixed login (ADR-0021; D-118..D-136).</summary>
public sealed class AccessTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spec007_AC01_WithoutMap_ThereIsNoDashboard()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using var app = await DashboardApp.StartAsync(env, s => s.Map = false);
        using var client = app.CreateClient();

        foreach (var path in (string[])["/psr", "/psr/", "/psr/api/auth", "/psr/api/stages", "/psr/hubs/runs/negotiate"])
        {
            (await client.GetAsync(path, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
        }
    }

    public static TheoryData<string> InvalidConfigurations() =>
        ["no mode", "two main modes", "fixed login without users", "host without a role", "invalid hash in configuration", "local requests outside Development"];

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public async Task Spec007_AC02_InvalidConfiguration_FailsTheStartup(string configuration)
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        Action<DashboardAppSettings> configure = configuration switch
        {
            "no mode" => s => s.Dashboard = _ => { }
            ,
            "two main modes" => s => s.Dashboard = o => o.UseFixedLogin(l => l.BindConfiguration()).UseAuthorizationFilter<HeaderFilter>(),
            "fixed login without users" => s => s.Dashboard = o => o.UseFixedLogin(),
            "host without a role" => s => s.Dashboard = o => o.UseHostAuthentication(r =>
            {
                r.Reader = p => p.RequireClaim("role", "r");
                r.Executor = p => p.RequireClaim("role", "e");
                r.Author = p => p.RequireClaim("role", "a");
            }),
            "invalid hash in configuration" => s =>
            {
                s.Configuration.Clear();
                foreach (var (key, value) in TestUsers.Configuration(("ana", DashboardRole.Admin, "senha-em-texto")))
                {
                    s.Configuration[key] = value;
                }
            }
            ,
            _ => s =>
            {
                s.Environment = "Production";
                s.Dashboard = o =>
                {
                    o.UseFixedLogin(l => l.BindConfiguration());
                    o.AllowLocalRequests = true;
                };
            }
            ,
        };

        var error = await Should.ThrowAsync<PsrConfigurationException>(async () =>
        {
            await using var app = await DashboardApp.StartAsync(env, configure);
        });

        error.Message.ShouldNotBeNullOrWhiteSpace();
        if (configuration == "invalid hash in configuration")
        {
            error.Message.ShouldContain("psr hash-password");
        }
    }

    [Fact]
    public async Task Spec007_AC03_Unauthenticated_Gets401JsonAndTheMode()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using (var app = await DashboardApp.StartAsync(env))
        {
            using var client = app.CreateClient();
            var denied = await client.GetAsync("/psr/api/stages", Ct);
            denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            denied.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
            (await denied.JsonAsync())["code"]!.GetValue<string>().ShouldBe("UNAUTHENTICATED");

            var auth = await (await client.GetAsync("/psr/api/auth", Ct)).JsonAsync();
            auth["mode"]!.GetValue<string>().ShouldBe("FixedLogin");
            auth["localRequests"]!.GetValue<bool>().ShouldBeFalse();
            auth.ToJsonString().ShouldNotContain("leitor");
        }

        await using (var host = await DashboardApp.StartAsync(env, s => HeaderAuthenticationHandler.Register(s)))
        {
            using var client = host.CreateClient();
            var denied = await client.GetAsync("/psr/api/stages", Ct);
            denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the API never follows the host challenge (redirect)");
            (await denied.JsonAsync())["code"]!.GetValue<string>().ShouldBe("UNAUTHENTICATED");

            var auth = await (await client.GetAsync("/psr/api/auth", Ct)).JsonAsync();
            auth["mode"]!.GetValue<string>().ShouldBe("Host");
            auth["loginUrl"]!.GetValue<string>().ShouldBe("/host/login?volta=%2Fpsr%2F");
            auth["logoutUrl"]!.GetValue<string>().ShouldBe("/host/logout");
        }
    }

    [Fact]
    public async Task Spec007_AC05_MutatingRequestWithoutTheHeader_Gets400()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using var app = await DashboardApp.StartAsync(env);
        using var client = app.CreateClient(csrf: false);

        var login = await client.PostAsJsonAsync("/psr/api/auth/login", new { name = "autor", password = TestUsers.Password }, Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await login.JsonAsync())["code"]!.GetValue<string>().ShouldBe("CSRF_HEADER_MISSING");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/psr/api/auth/login")
        {
            Content = JsonContent.Create(new { name = "autor", password = TestUsers.Password }),
        };
        request.Headers.Add("X-PSR-Request", "1");
        (await client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var create = await client.PostAsJsonAsync("/psr/api/stages", new { key = "sem-cabecalho", name = "x" }, Ct);
        create.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await create.JsonAsync())["code"]!.GetValue<string>().ShouldBe("CSRF_HEADER_MISSING");
        (await app.Admin.GetStageAsync("sem-cabecalho", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Spec007_AC17_FixedLogin_SetsTheSessionCookieAndLogoutEndsIt()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using var app = await DashboardApp.StartAsync(env);
        using var client = app.CreateClient();

        var login = await client.PostAsJsonAsync("/psr/api/auth/login", new { name = "AUTOR", password = TestUsers.Password }, Ct);

        login.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("psr.session=", StringComparison.Ordinal)).ToLowerInvariant();
        cookie.ShouldContain("httponly");
        cookie.ShouldContain("samesite=lax");
        cookie.ShouldContain("path=/psr");
        var me = await (await client.GetAsync("/psr/api/me", Ct)).JsonAsync();
        me["name"]!.GetValue<string>().ShouldBe("autor");
        me["subject"]!.GetValue<string>().ShouldBe("login:autor");
        me["role"]!.GetValue<string>().ShouldBe("Author");
        me["roles"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["Reader", "Executor", "Author"]);
        me["mode"]!.GetValue<string>().ShouldBe("FixedLogin");

        (await client.PostAsync("/psr/api/auth/logout", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync("/psr/api/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Spec007_AC18_AC23_FailuresLockTheUser_IpsAreLimited_AndLoginsAreAudited()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python, time);
        await using var app = await DashboardApp.StartAsync(env);

        async Task<HttpResponseMessage> LoginAsync(string name, string password, string ip)
        {
            using var client = app.CreateClient(ip: ip);
            return await client.PostAsJsonAsync("/psr/api/auth/login", new { name, password }, Ct);
        }

        // A successful login and a logout are audited.
        using (var session = await app.LoginAsync("leitor"))
        {
            (await session.PostAsync("/psr/api/auth/logout", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // 5 consecutive failures lock the user (each from another IP: the IP limit is not reached).
        var generic = await LoginAsync("ninguem", "x", "198.51.100.1");
        generic.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var genericBody = (await generic.JsonAsync()).AsObject();
        for (var i = 0; i < 5; i++)
        {
            (await LoginAsync("executor", "errada", $"198.51.100.{10 + i}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var locked = await LoginAsync("executor", TestUsers.Password, "198.51.100.30");
        locked.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the right password gets the same answer while locked");
        var lockedBody = (await locked.JsonAsync()).AsObject();
        lockedBody["code"]!.GetValue<string>().ShouldBe(genericBody["code"]!.GetValue<string>());
        lockedBody["detail"]?.ToString().ShouldBe(genericBody["detail"]?.ToString());

        time.Advance(TimeSpan.FromMinutes(14));
        (await LoginAsync("executor", TestUsers.Password, "198.51.100.31")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        time.Advance(TimeSpan.FromMinutes(1));
        (await LoginAsync("executor", TestUsers.Password, "198.51.100.32")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The 11th attempt in the same minute from one IP gets 429 with Retry-After.
        for (var i = 0; i < 10; i++)
        {
            (await LoginAsync("ninguem", "x", "192.0.2.77")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var limited = await LoginAsync("leitor", TestUsers.Password, "192.0.2.77");
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter!.Delta!.Value.ShouldBeGreaterThan(TimeSpan.Zero);
        time.Advance(TimeSpan.FromMinutes(1));
        (await LoginAsync("leitor", TestUsers.Password, "192.0.2.77")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var actions = await env.ColumnAsync<string>("SELECT action || ' ' || actor || ' ' || entity_key FROM psr.audit_events ORDER BY occurred_at, id");
        actions.ShouldBe(
        [
            "LoginSucceeded login:leitor leitor",
            "LoggedOut login:leitor leitor",
            "UserLocked login:executor executor",
            "LoginSucceeded login:executor executor",
            "LoginSucceeded login:leitor leitor",
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task Spec007_AC19_Session_ExpiresAfterIdleTimeOrTheAbsoluteLimit()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python, time);
        await using var app = await DashboardApp.StartAsync(env);

        using (var idle = await app.LoginAsync("leitor"))
        {
            time.Advance(TimeSpan.FromHours(7.9));
            (await idle.GetAsync("/psr/api/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "used before 8 h: the session slides");
            time.Advance(TimeSpan.FromHours(7.9));
            (await idle.GetAsync("/psr/api/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            time.Advance(TimeSpan.FromHours(8.1));
            (await idle.GetAsync("/psr/api/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "8 h without use");
        }

        using var busy = await app.LoginAsync("leitor");
        for (var hour = 0; hour < 23; hour++)
        {
            time.Advance(TimeSpan.FromHours(1));
            (await busy.GetAsync("/psr/api/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        time.Advance(TimeSpan.FromHours(1));
        (await busy.GetAsync("/psr/api/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "24 h since the login, even when used");
    }

    [Fact]
    public async Task Spec007_AC20_ChangedConfiguration_AppliesWithoutRestart()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var reloadable = new ReloadableConfigurationSource();
        await using var app = await DashboardApp.StartAsync(env, s =>
        {
            s.Configuration.Clear();
            s.Reloadable = reloadable;
            reloadable.Provider.Replace(TestUsers.Configuration());
        });
        using var removed = await app.LoginAsync("leitor");
        using var changedPassword = await app.LoginAsync("executor");
        using var promoted = await app.LoginAsync("autor");
        var newHash = DashboardPasswords.Hash("nova-senha");

        reloadable.Provider.Replace(TestUsers.Configuration(
            ("executor", DashboardRole.Executor, newHash), ("autor", DashboardRole.Admin, null), ("admin", DashboardRole.Admin, null)));

        await Eventually.WaitAsync(async () => (await removed.GetAsync("/psr/api/me", Ct)).StatusCode == HttpStatusCode.Unauthorized, TimeSpan.FromSeconds(5));
        (await changedPassword.GetAsync("/psr/api/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await (await promoted.GetAsync("/psr/api/me", Ct)).JsonAsync())["role"]!.GetValue<string>().ShouldBe("Admin");
        (await promoted.GetAsync("/psr/api/audit", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var relogged = await app.LoginAsync("executor", "nova-senha");

        // An invalid configuration is refused: the previous users stay.
        reloadable.Provider.Replace(TestUsers.Configuration(("admin", DashboardRole.Admin, "texto-puro")));
        await Task.Delay(200, Ct);
        (await relogged.GetAsync("/psr/api/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var stillValid = await app.LoginAsync("executor", "nova-senha");
    }

    [Fact]
    public async Task Spec007_AC21_CustomFilter_GivesNameAndRole()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using var app = await DashboardApp.StartAsync(env, s => s.Dashboard = o => o.UseAuthorizationFilter<HeaderFilter>());
        await app.PublishStageAsync("eco", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);
        using var anonymous = app.CreateClient();
        using var ana = app.CreateClient();
        ana.DefaultRequestHeaders.Add(HeaderFilter.Header, "ana|Executor");

        (await anonymous.GetAsync("/psr/api/stages", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await (await anonymous.GetAsync("/psr/api/auth", Ct)).JsonAsync())["mode"]!.GetValue<string>().ShouldBe("Filter");
        var me = await (await ana.GetAsync("/psr/api/me", Ct)).JsonAsync();
        me["subject"]!.GetValue<string>().ShouldBe("filter:ana");
        me["role"]!.GetValue<string>().ShouldBe("Executor");
        (await ana.PostAsJsonAsync("/psr/api/stages", new { key = "nova", name = "nova" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var run = await ana.PostAsJsonAsync("/psr/api/stages/eco/runs", new { inputs = new { texto = "oi" } }, Ct);
        run.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var runId = (await run.JsonAsync())["runId"]!.GetValue<Guid>();

        (await env.ScalarAsync<string>($"SELECT triggered_by FROM psr.runs WHERE id = '{runId}'")).ShouldBe("filter:ana");
        (await env.ScalarAsync<string>($"SELECT actor FROM psr.audit_events WHERE action = 'RunTriggered' AND entity_id = '{runId}'")).ShouldBe("filter:ana");
    }

    [Fact]
    public async Task Spec007_AC22_LocalRequests_EnterAsAdminInDevelopment()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using (var app = await DashboardApp.StartAsync(env, s =>
        {
            s.Environment = "Development";
            s.Dashboard = o =>
            {
                o.UseFixedLogin(l => l.BindConfiguration());
                o.AllowLocalRequests = true;
            };
        }))
        {
            using var local = app.CreateClient(ip: "127.0.0.1");
            var me = await (await local.GetAsync("/psr/api/me", Ct)).JsonAsync();
            me["subject"]!.GetValue<string>().ShouldBe("local");
            me["role"]!.GetValue<string>().ShouldBe("Admin");
            me["mode"]!.GetValue<string>().ShouldBe("Localhost");
            (await local.GetAsync("/psr/api/audit", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

            using var remote = app.CreateClient(ip: "203.0.113.50");
            (await remote.GetAsync("/psr/api/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var auth = await (await remote.GetAsync("/psr/api/auth", Ct)).JsonAsync();
            auth["mode"]!.GetValue<string>().ShouldBe("FixedLogin");
            auth["localRequests"]!.GetValue<bool>().ShouldBeTrue();
            using var reader = await app.LoginAsync("leitor");
            (await (await reader.GetAsync("/psr/api/me", Ct)).JsonAsync())["role"]!.GetValue<string>().ShouldBe("Reader");
        }

        await using var localOnly = await DashboardApp.StartAsync(env, s =>
        {
            s.Environment = "Development";
            s.Dashboard = o => o.AllowLocalRequests = true;
        });
        using var outsider = localOnly.CreateClient(ip: "203.0.113.51");
        (await outsider.GetAsync("/psr/api/stages", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await (await outsider.GetAsync("/psr/api/auth", Ct)).JsonAsync())["mode"]!.GetValue<string>().ShouldBe("Localhost");
        using var ipv6 = localOnly.CreateClient(ip: "::1");
        (await ipv6.GetAsync("/psr/api/stages", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Spec007_AC24_SessionIsValidOnOtherInstancesAndAfterRestart()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        string cookie;
        await using (var first = await DashboardApp.StartAsync(env))
        {
            using var session = await first.LoginAsync("autor");
            cookie = session.CookieHeader("/psr/api/me");
            cookie.ShouldStartWith("psr.session=");
        }

        await using var second = await DashboardApp.StartAsync(env);
        await using var third = await DashboardApp.StartAsync(env);

        foreach (var app in (DashboardApp[])[second, third])
        {
            using var client = app.CreateClient();
            client.DefaultRequestHeaders.Add("Cookie", cookie);
            var me = await client.GetAsync("/psr/api/me", Ct);
            me.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await me.JsonAsync())["name"]!.GetValue<string>().ShouldBe("autor");
        }

        (await env.ScalarAsync<long>("SELECT count(*) FROM psr.dashboard_keys")).ShouldBe(1);
    }
}
