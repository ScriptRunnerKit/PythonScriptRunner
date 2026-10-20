using System.Net;
using System.Text;
using PythonScriptRunner.Dashboard.Tests.Infrastructure;

namespace PythonScriptRunner.Dashboard.Tests;

/// <summary>Spec 007, AC-4: every endpoint × role gives 2xx (or a business answer) or 403, in the fixed login and host modes.</summary>
public sealed class AuthorizationMatrixTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python)
{
    private static readonly string Unknown = Guid.CreateVersion7().ToString();

    /// <summary>Method, path, body, lowest role (<c>null</c>: any identified user) and whether an allowed call must be 2xx.</summary>
    private static readonly (string Method, string Path, string? Body, DashboardRole? Minimum, bool Ok)[] Matrix =
    [
        ("GET", "/me", null, null, true),
        ("GET", "/stages", null, DashboardRole.Reader, true),
        ("GET", "/stages/eco-{role}", null, DashboardRole.Reader, true),
        ("GET", "/stages/eco-{role}/versions/1", null, DashboardRole.Reader, true),
        ("GET", "/stages/eco-{role}/versions/1/export", null, DashboardRole.Reader, true),
        ("GET", "/pipelines", null, DashboardRole.Reader, true),
        ("GET", "/pipelines/trilha-{role}", null, DashboardRole.Reader, true),
        ("GET", "/pipelines/trilha-{role}/versions/1", null, DashboardRole.Reader, true),
        ("GET", "/pipelines/trilha-{role}/versions/1/export", null, DashboardRole.Reader, true),
        ("GET", "/overview", null, DashboardRole.Reader, true),
        ("GET", "/runs", null, DashboardRole.Reader, true),
        ($"GET", $"/runs/{Unknown}", null, DashboardRole.Reader, false),
        ("GET", $"/runs/{Unknown}/attempts/{Unknown}/logs/stdout", null, DashboardRole.Reader, false),
        ("GET", $"/artifacts/{Unknown}", null, DashboardRole.Reader, false),
        ("GET", "/schedules", null, DashboardRole.Reader, true),
        ("POST", "/schedules/preview", """{"cron":"0 8 * * *","timeZone":"America/Sao_Paulo","count":3}""", DashboardRole.Reader, true),
        ("GET", "/meta/field-types", null, DashboardRole.Reader, true),
        ("POST", "/stages/eco-{role}/runs", """{"inputs":{"texto":"oi"}}""", DashboardRole.Executor, true),
        ("POST", "/pipelines/trilha-{role}/runs", """{"inputs":{"texto":"oi"}}""", DashboardRole.Executor, true),
        ("POST", $"/runs/{Unknown}/cancel", null, DashboardRole.Executor, false),
        ("POST", $"/runs/{Unknown}/reprocess", null, DashboardRole.Executor, false),
        ("POST", "/stages", """{"key":"nova-{role}","name":"Nova"}""", DashboardRole.Author, true),
        ("PATCH", "/stages/eco-{role}", """{"name":"Eco"}""", DashboardRole.Author, false),
        ("GET", "/stages/eco-{role}/draft", null, DashboardRole.Author, false),
        ("POST", "/stages/eco-{role}/draft", """{"fromVersion":1}""", DashboardRole.Author, false),
        ("PUT", "/stages/eco-{role}/draft", """{"script":"x","pythonVersion":"3.12"}""", DashboardRole.Author, false),
        ("POST", "/stages/eco-{role}/draft/validate", null, DashboardRole.Author, false),
        ("POST", "/stages/eco-{role}/draft/test", """{"inputs":{"texto":"oi"}}""", DashboardRole.Author, false),
        ("POST", "/stages/eco-{role}/draft/publish", null, DashboardRole.Author, false),
        ("DELETE", "/stages/eco-{role}/draft", null, DashboardRole.Author, false),
        ("POST", "/pipelines", """{"key":"nova-{role}","name":"Nova"}""", DashboardRole.Author, true),
        ("POST", "/pipelines/validate", """{"steps":[]}""", DashboardRole.Author, true),
        ("GET", "/pipelines/trilha-{role}/draft", null, DashboardRole.Author, false),
        ("POST", "/pipelines/trilha-{role}/draft", """{"fromVersion":1}""", DashboardRole.Author, false),
        ("PUT", "/pipelines/trilha-{role}/draft", """{"steps":[]}""", DashboardRole.Author, false),
        ("POST", "/pipelines/trilha-{role}/draft/test", """{"inputs":{"texto":"oi"}}""", DashboardRole.Author, false),
        ("POST", "/pipelines/trilha-{role}/draft/publish", null, DashboardRole.Author, false),
        ("DELETE", "/pipelines/trilha-{role}/draft", null, DashboardRole.Author, false),
        ("PATCH", "/pipelines/trilha-{role}/settings", """{"maxConcurrentRuns":2}""", DashboardRole.Author, false),
        ("POST", "/pipelines/trilha-{role}/schedules", """{"name":"diário {role}","cron":"0 8 * * *","inputs":{"texto":"oi"}}""", DashboardRole.Author, true),
        ("PUT", $"/schedules/{Unknown}", """{"name":"x","cron":"0 8 * * *"}""", DashboardRole.Author, false),
        ("POST", $"/schedules/{Unknown}/enable", null, DashboardRole.Author, false),
        ("POST", $"/schedules/{Unknown}/disable", null, DashboardRole.Author, false),
        ("DELETE", $"/schedules/{Unknown}", null, DashboardRole.Author, false),
        ("POST", "/import", null, DashboardRole.Author, false),
        ("GET", "/audit", null, DashboardRole.Admin, true),
        ("GET", "/admin/secrets", null, DashboardRole.Admin, true),
        ("GET", "/admin/settings", null, DashboardRole.Admin, true),
        ("PATCH", "/pipelines/trilha-{role}/retention", """{"artifactRetentionDays":3,"runRetentionDays":30}""", DashboardRole.Admin, false),
        ("POST", "/stages/eco-{role}/versions/1/archive", null, DashboardRole.Author, false),
        ("POST", "/pipelines/trilha-{role}/versions/1/archive", null, DashboardRole.Author, false),
        ("DELETE", "/pipelines/trilha-{role}", null, DashboardRole.Admin, false),
        ("DELETE", "/stages/eco-{role}", null, DashboardRole.Admin, false),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spec007_AC04_RoleMatrix_FixedLogin()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using var app = await DashboardApp.StartAsync(env);
        await SeedAsync(app);
        foreach (var (name, role) in TestUsers.All)
        {
            using var client = await app.LoginAsync(name);
            await CheckAsync(client, role);
        }
    }

    [Fact]
    public async Task Spec007_AC04_RoleMatrix_HostAuthentication()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using var app = await DashboardApp.StartAsync(env, s => HeaderAuthenticationHandler.Register(s));
        await SeedAsync(app);

        // Authenticated by the host without any dashboard role: identified, but every action is forbidden.
        using (var nobody = app.CreateClient())
        {
            nobody.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.Header, "sub-0|Sem Papel|outra-coisa");
            var me = await (await nobody.GetAsync("/psr/api/me", Ct)).JsonAsync();
            me["subject"]!.GetValue<string>().ShouldBe("sub-0");
            me["role"].ShouldBeNull();
            (await nobody.GetAsync("/psr/api/stages", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        foreach (var (_, role) in TestUsers.All)
        {
            using var client = app.CreateClient();
            client.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.Header, $"sub-{role}|Pessoa {role}|{HeaderAuthenticationHandler.RoleClaim(role)}");
            await CheckAsync(client, role);
        }
    }

    /// <summary>
    /// Per role (the destructive calls of one role must not change what the next one sees): a published stage <c>eco-&lt;role&gt;</c>
    /// and pipeline <c>trilha-&lt;role&gt;</c>, each with a draft of version 2.
    /// </summary>
    private static async Task SeedAsync(DashboardApp app)
    {
        foreach (var (_, role) in TestUsers.All)
        {
            var suffix = role.ToString().ToLowerInvariant();
            await app.PublishStageAsync($"eco-{suffix}", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);
            var pipeline = await app.Admin.CreatePipelineAsync(new NewCatalogItem($"trilha-{suffix}", "Trilha"), PsrEnvironment.Author, Ct);
            var saved = await app.Admin.SavePipelineDraftAsync($"trilha-{suffix}", pipeline.Version,
                Scripts.SingleStep($"eco-{suffix}", 1, [Scripts.Text("texto")]), pipeline.RowVersion, PsrEnvironment.Author, Ct);
            await app.Admin.PublishPipelineAsync($"trilha-{suffix}", saved.Version, saved.RowVersion, PsrEnvironment.Author, Ct);
            await app.Admin.CreateStageDraftFromAsync($"eco-{suffix}", 1, PsrEnvironment.Author, Ct);
            await app.Admin.CreatePipelineDraftFromAsync($"trilha-{suffix}", 1, PsrEnvironment.Author, Ct);
        }
    }

    private static async Task CheckAsync(HttpClient client, DashboardRole role)
    {
        var failures = new List<string>();
        foreach (var (method, path, body, minimum, ok) in Matrix)
        {
            var suffix = role.ToString().ToLowerInvariant();
            using var request = new HttpRequestMessage(new HttpMethod(method), "/psr/api" + path.Replace("{role}", suffix, StringComparison.Ordinal));
            if (body is not null)
            {
                request.Content = new StringContent(body.Replace("{role}", suffix, StringComparison.Ordinal), Encoding.UTF8,
                    "application/json");
            }

            if (method is "PUT" or "PATCH" or "DELETE" || path.EndsWith("/publish", StringComparison.Ordinal))
            {
                request.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue("\"1\""));
            }

            using var response = await client.SendAsync(request, Ct);
            var status = (int)response.StatusCode;
            var allowed = minimum is null || role >= minimum;
            var expectation = allowed ? (ok ? "2xx" : "not 401/403") : "403";
            var met = allowed ? (ok ? status is >= 200 and < 300 : status is not (401 or 403)) : status == 403;
            if (!met)
            {
                failures.Add($"{role} {method} {path}: {status} (expected {expectation}) {await response.Content.ReadAsStringAsync(Ct)}");
            }
        }

        failures.ShouldBeEmpty();
    }
}
