using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using PythonScriptRunner.Dashboard.Tests.Infrastructure;

namespace PythonScriptRunner.Dashboard.Tests;

/// <summary>Spec 007: custom prefix (AC-14), the panel page and the internal OpenAPI document (AC-15).</summary>
public sealed class PrefixAndOpenApiTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spec007_AC14_CustomPrefix_ApiHubCookieAndPanelUseIt()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using var app = await DashboardApp.StartAsync(env, s => s.Prefix = "/admin/python");
        using var client = app.CreateClient();

        var login = await client.PostAsJsonAsync("/admin/python/api/auth/login", new { name = "leitor", password = TestUsers.Password }, Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("psr.session=", StringComparison.Ordinal)).ToLowerInvariant()
            .ShouldContain("path=/admin/python");
        (await (await client.GetAsync("/admin/python/api/me", Ct)).JsonAsync())["role"]!.GetValue<string>().ShouldBe("Reader");
        (await client.GetAsync("/psr/api/auth", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var page = await client.GetAsync("/admin/python/execucoes/123", Ct);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        page.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        (await page.Content.ReadAsStringAsync(Ct)).ShouldContain("<base href=\"/admin/python/\">");
        page.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("frame-ancestors 'none'");
        page.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        page.Headers.GetValues("Referrer-Policy").Single().ShouldBe("same-origin");

        var unknown = await client.GetAsync("/admin/python/api/nao-existe", Ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknown.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        var cookie = client.CookieHeader("/admin/python/hubs/runs");
        cookie.ShouldStartWith("psr.session=");
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/admin/python/hubs/runs"), o =>
            {
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.Headers["Cookie"] = cookie;
            })
            .Build();
        await hub.StartAsync(Ct);
        await hub.InvokeAsync("SubscribeAll", Ct);

        await using var anonymous = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/admin/python/hubs/runs"), o =>
            {
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        (await Should.ThrowAsync<HttpRequestException>(() => anonymous.StartAsync(Ct))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Spec007_AC15_OpenApiDocument_MatchesTheSnapshot()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using (var production = await DashboardApp.StartAsync(env))
        {
            using var client = production.CreateClient();
            (await client.GetAsync("/psr/api/openapi/v1.json", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "only in Development");
        }

        await using var app = await DashboardApp.StartAsync(env, s => s.Environment = "Development");
        using var development = app.CreateClient();
        var document = await development.GetAsync("/psr/api/openapi/v1.json", Ct);

        document.StatusCode.ShouldBe(HttpStatusCode.OK);
        await Snapshot.MatchJsonAsync("openapi-v1", await document.Content.ReadAsStringAsync(Ct));
    }
}
