using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using PythonScriptRunner.Dashboard.Tests.Infrastructure;

namespace PythonScriptRunner.Dashboard.Tests;

/// <summary>Spec 007: catalog editing through the API (drafts, concurrency, publication, secrets).</summary>
public sealed class CatalogApiTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spec007_AC06_TwoSavesWithTheSameIfMatch_TheSecondGets412WithTheCurrentDraft()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using var app = await DashboardApp.StartAsync(env);
        using var author = await app.LoginAsync("autor");
        (await author.PostAsJsonAsync("/psr/api/stages", new { key = "conflito", name = "Conflito" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var read = await author.GetAsync("/psr/api/stages/conflito/draft", Ct);
        var etag = read.ETag();
        var data = (await read.JsonAsync())["data"]!.AsObject();

        data["script"] = Scripts.Echo;
        var first = await author.SendJsonAsync(HttpMethod.Put, "/psr/api/stages/conflito/draft", data, etag);
        data["script"] = Scripts.Sleep;
        var second = await author.SendJsonAsync(HttpMethod.Put, "/psr/api/stages/conflito/draft", data, etag);
        var withoutIfMatch = await author.SendJsonAsync(HttpMethod.Put, "/psr/api/stages/conflito/draft", data);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.ETag().ShouldNotBe(etag);
        second.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        var problem = await second.JsonAsync();
        problem["code"]!.GetValue<string>().ShouldBe("CONCURRENCY_CONFLICT");
        problem["current"]!["data"]!["script"]!.GetValue<string>().ShouldBe(Scripts.Echo);
        withoutIfMatch.StatusCode.ShouldBe((HttpStatusCode)428);
        (await withoutIfMatch.JsonAsync())["code"]!.GetValue<string>().ShouldBe("IF_MATCH_REQUIRED");
    }

    [Fact]
    public async Task Spec007_AC07_AC12_PublishWithSyntaxError_Gets422WithLineAndColumn_PublicationIsAudited()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using var app = await DashboardApp.StartAsync(env);
        using var author = await app.LoginAsync("autor");
        await author.PostAsJsonAsync("/psr/api/stages", new { key = "quebrado", name = "Quebrado" }, Ct);
        var draft = await author.GetAsync("/psr/api/stages/quebrado/draft", Ct);
        var data = (await draft.JsonAsync())["data"]!.AsObject();
        data["script"] = "from psrunner import stage\n\n\n@stage\ndef run(inputs, ctx):\n    return {\"x\": (1 +}\n";
        var saved = await author.SendJsonAsync(HttpMethod.Put, "/psr/api/stages/quebrado/draft", data, draft.ETag());

        var publish = await author.SendJsonAsync(HttpMethod.Post, "/psr/api/stages/quebrado/draft/publish", ifMatch: saved.ETag());

        publish.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await publish.JsonAsync();
        problem["code"]!.GetValue<string>().ShouldBe("SCRIPT_SYNTAX_ERROR");
        problem["line"]!.GetValue<int>().ShouldBe(6);
        problem["column"]!.GetValue<int>().ShouldBeGreaterThan(0);
        (await app.Admin.GetStageDraftAsync("quebrado", Ct)).ShouldNotBeNull("the draft is kept");

        data["script"] = Scripts.Echo;
        data["inputs"] = new JsonArray(new JsonObject { ["name"] = "texto", ["type"] = "string" });
        data["outputs"] = new JsonArray(new JsonObject { ["name"] = "texto", ["type"] = "string" });
        var fixedDraft = await author.SendJsonAsync(HttpMethod.Put, "/psr/api/stages/quebrado/draft", data, saved.ETag());
        fixedDraft.StatusCode.ShouldBe(HttpStatusCode.OK);
        var published = await author.SendJsonAsync(HttpMethod.Post, "/psr/api/stages/quebrado/draft/publish", ifMatch: fixedDraft.ETag());
        published.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await published.JsonAsync())["status"]!.GetValue<string>().ShouldBe("Published");

        (await env.ScalarAsync<string>("SELECT actor FROM psr.audit_events WHERE action = 'StagePublished' AND entity_key = 'quebrado@1'"))
            .ShouldBe("login:autor");
    }

    [Fact]
    public async Task Spec007_AC16_Secrets_ShowStatusButNeverValues()
    {
        const string value = "valor-super-secreto-9f3a";
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using var app = await DashboardApp.StartAsync(env, s => s.Configuration["PythonScriptRunner:Secrets:SMTP_SENHA"] = value);
        await app.PublishStageAsync("envia", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")], ["SMTP_SENHA", "API_TOKEN"]);
        await app.PublishStageAsync("outra", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")], ["SMTP_SENHA"]);
        using var admin = await app.LoginAsync("admin");

        var response = await admin.GetAsync("/psr/api/admin/secrets", Ct);
        var settings = await admin.GetStringAsync("/psr/api/admin/settings", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync(Ct);
        text.ShouldNotContain(value);
        settings.ShouldNotContain(value);
        settings.ShouldNotContain(TestUsers.Hash);
        var secrets = JsonNode.Parse(text)!.AsArray();
        secrets.Select(s => (s!["name"]!.GetValue<string>(), s["resolved"]!.GetValue<bool>()))
            .ShouldBe([("API_TOKEN", false), ("SMTP_SENHA", true)]);
        secrets[1]!["declaredBy"]!.AsArray().Select(d => d!.GetValue<string>()).ShouldBe(["envia@1", "outra@1"]);
    }

    [Fact]
    public async Task FieldTypes_DescribeTheTwelveTypesAndTheCompatibility()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using var app = await DashboardApp.StartAsync(env);
        using var reader = await app.LoginAsync("leitor");

        var types = await (await reader.GetAsync("/psr/api/meta/field-types", Ct)).JsonAsync();

        types["types"]!.AsArray().Count.ShouldBe(12);
        types["compatibility"]!["integer"]!.AsArray().Select(t => t!.GetValue<string>()).ShouldBe(["number", "integer", "decimal", "json"], ignoreOrder: true);
        types["compatibility"]!["file"]!.AsArray().Select(t => t!.GetValue<string>()).ShouldBe(["file"]);
    }
}
