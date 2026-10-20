using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Time.Testing;
using PythonScriptRunner.Dashboard.Tests.Infrastructure;

namespace PythonScriptRunner.Dashboard.Tests;

/// <summary>Spec 007: runs, uploads, downloads, lists and the overview through the API.</summary>
public sealed class RunApiTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python)
{
    private const string Copy = """
        import shutil

        from psrunner import stage


        @stage
        def run(inputs, ctx):
            original = inputs["arquivo"]
            copia = ctx.new_file("copia-" + original.name, original.content_type)
            shutil.copyfile(original.path, copia.path)
            return {"copia": copia}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spec007_AC08_UploadRunsAndTheOutputIsDownloadable_TooLargeGets413WithoutARun()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await env.StartHostAsync(s => s.RecordEvents = false);
        await using var app = await DashboardApp.StartAsync(env);
        await app.PublishStageAsync("copia", Copy, [Scripts.File("arquivo")], [Scripts.File("copia")]);
        using var executor = await app.LoginAsync("executor");
        var content = Encoding.UTF8.GetBytes(new string('x', 10 * 1024));

        using var form = new MultipartFormDataContent
        {
            { new StringContent("{}", Encoding.UTF8, "application/json"), "inputs" },
            { new ByteArrayContent(content) { Headers = { ContentType = new MediaTypeHeaderValue("text/plain") } }, "arquivo", "relatorio.txt" },
        };
        var accepted = await executor.PostAsync("/psr/api/stages/copia/runs", form, Ct);

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync(Ct));
        var runId = RunId.Parse((await accepted.JsonAsync())["runId"]!.GetValue<string>());
        accepted.Headers.Location!.ToString().ShouldBe($"/psr/api/runs/{runId}");
        RunSnapshot? run = null;
        await Eventually.WaitAsync(async () => (run = await app.Runs.GetAsync(runId, Ct))?.Status is RunStatus.Succeeded or RunStatus.Failed,
            TimeSpan.FromSeconds(90), () => $"run is {run?.Status}");
        run!.Status.ShouldBe(RunStatus.Succeeded, run.Error?.Message);
        var detail = await (await executor.GetAsync($"/psr/api/runs/{runId}", Ct)).JsonAsync();
        var output = detail["artifacts"]!.AsArray().Single(a => a!["direction"]!.GetValue<string>() == "Output")!;
        var download = await executor.GetAsync($"/psr/api/artifacts/{output["id"]!.GetValue<string>()}", Ct);
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        download.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        download.Content.Headers.ContentDisposition.FileNameStar.ShouldBe("copia-relatorio.txt");
        download.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        (await download.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(content);
        var attempt = run.Steps.Single().Attempts.Single();
        (await executor.GetAsync($"/psr/api/runs/{runId}/attempts/{attempt.Id}/logs/stdout", Ct)).StatusCode
            .ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);

        // 51 MB: refused before any run is created.
        var runs = await env.ScalarAsync<long>("SELECT count(*) FROM psr.runs");
        using var large = new MultipartFormDataContent
        {
            { new StringContent("{}", Encoding.UTF8, "application/json"), "inputs" },
            { new ByteArrayContent(new byte[51 * 1024 * 1024]), "arquivo", "grande.bin" },
        };
        var tooLarge = await executor.PostAsync("/psr/api/stages/copia/runs", large, Ct);
        tooLarge.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await tooLarge.JsonAsync())["code"]!.GetValue<string>().ShouldBe("PAYLOAD_TOO_LARGE");
        (await env.ScalarAsync<long>("SELECT count(*) FROM psr.runs")).ShouldBe(runs);
        Directory.EnumerateFiles(Path.Combine(env.WorkRoot, app.Settings.InstanceId, "uploads")).ShouldBeEmpty("temporary uploads are deleted");
    }

    [Fact]
    public async Task Spec007_AC08_HtmlToEmail_RunsFromThePanelAndThePdfIsDownloadable()
    {
        // weasyprint needs pango (GTK) at runtime: the CI runs this test on Linux (D-106).
        Assert.SkipWhen(OperatingSystem.IsWindows(), "weasyprint needs GTK/pango; CS-2 runs on Linux (D-106).");
        const ushort smtpPort = 1025;
        await using var mailpit = new ContainerBuilder("axllent/mailpit:v1.27.0")
            .WithEnvironment("MP_SMTP_AUTH_ACCEPT_ANY", "1")
            .WithEnvironment("MP_SMTP_AUTH_ALLOW_INSECURE", "1")
            .WithPortBinding(smtpPort, true)
            .WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/livez")))
            .Build();
        await mailpit.StartAsync(Ct);
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var worker = await env.StartHostAsync(s =>
        {
            s.RecordEvents = false;
            s.Configuration["PythonScriptRunner:Secrets:SMTP_USUARIO"] = "psr";
            s.Configuration["PythonScriptRunner:Secrets:SMTP_SENHA"] = "segredo-de-teste";
        });
        await env.ImportFixtureAsync(worker, "html-para-email", (path, content) => path.EndsWith(".pipeline.json", StringComparison.Ordinal)
            ? content.Replace("smtp.exemplo.com", mailpit.Hostname, StringComparison.Ordinal)
                .Replace("\"const\": 465", $"\"const\": {mailpit.GetMappedPublicPort(smtpPort)}", StringComparison.Ordinal)
            : content);
        await using var app = await DashboardApp.StartAsync(env);
        using var executor = await app.LoginAsync("executor");
        var html = "<html><body><h1>Relatório</h1><p>" + new string('x', 10 * 1024) + "</p></body></html>";

        using var form = new MultipartFormDataContent
        {
            { JsonContent.Create(new { destinatarios = new[] { "financeiro@exemplo.com" }, assunto = "Relatório" }), "inputs" },
            { new StringContent(html, Encoding.UTF8, "text/html"), "html", "pagina.html" },
        };
        var accepted = await executor.PostAsync("/psr/api/pipelines/html-para-email/runs", form, Ct);

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync(Ct));
        var run = await worker.WaitForRunAsync(RunId.Parse((await accepted.JsonAsync())["runId"]!.GetValue<string>()), timeout: TimeSpan.FromMinutes(5));
        run.Status.ShouldBe(RunStatus.Succeeded, run.Error?.Message);
        var pdf = run.Artifacts.Single(a => a.Direction == ArtifactDirection.Output && a.FieldName == "pdf");
        var download = await executor.GetAsync($"/psr/api/artifacts/{pdf.Id}", Ct);
        download.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        Encoding.ASCII.GetString((await download.Content.ReadAsByteArrayAsync(Ct))[..4]).ShouldBe("%PDF");
    }

    [Fact]
    public async Task Spec007_AC09_Runs_WindowIsLimitedAndTheCursorWalksEveryRunOnce()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using var app = await DashboardApp.StartAsync(env);
        await app.PublishStageAsync("eco", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);
        var queue = app.Services.GetRequiredService<IRunQueue>();
        var created = new HashSet<RunId>();
        for (var i = 0; i < 120; i++)
        {
            created.Add((await queue.EnqueueStageAsync("eco", new { texto = $"t{i}" }, cancellationToken: Ct)).RunId);
        }

        using var reader = await app.LoginAsync("leitor");
        var now = DateTimeOffset.UtcNow;
        var tooLong = await reader.GetAsync($"/psr/api/runs?from={Uri.EscapeDataString(now.AddDays(-120).ToString("O"))}", Ct);
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var seen = new List<RunId>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await (await reader.GetAsync($"/psr/api/runs?limit=50{(cursor is null ? "" : $"&cursor={cursor}")}", Ct)).JsonAsync();
            seen.AddRange(page["items"]!.AsArray().Select(r => RunId.Parse(r!["id"]!.GetValue<string>())));
            cursor = page["nextCursor"]?.GetValue<string>();
            pages++;
        }
        while (cursor is not null);

        pages.ShouldBe(3);
        seen.Count.ShouldBe(120);
        seen.ToHashSet().SetEquals(created).ShouldBeTrue();
    }

    [Fact]
    public async Task Spec007_AC10_ExpiredArtifact_Gets410()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using var app = await DashboardApp.StartAsync(env);
        await app.PublishStageAsync("copia", Copy, [Scripts.File("arquivo")], [Scripts.File("copia")]);
        var handle = await app.Services.GetRequiredService<IRunQueue>()
            .EnqueueStageAsync("copia", new { arquivo = PsrFile.FromText("conteúdo", "a.txt") }, cancellationToken: Ct);
        var artifact = (await app.Runs.GetAsync(handle.RunId, Ct))!.Artifacts.Single();
        using var reader = await app.LoginAsync("leitor");
        (await reader.GetAsync($"/psr/api/artifacts/{artifact.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await env.ScalarAsync<int>($"WITH u AS (UPDATE psr.artifacts SET content_deleted_at = now() WHERE id = '{artifact.Id}' RETURNING 1) SELECT count(*)::int FROM u");
        var expired = await reader.GetAsync($"/psr/api/artifacts/{artifact.Id}", Ct);

        expired.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await expired.JsonAsync())["code"]!.GetValue<string>().ShouldBe("ARTIFACT_EXPIRED");
    }

    [Fact]
    public async Task Spec007_AC12_TriggerAndCancelFromThePanel_AreAuditedWithThePanelUser()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        await using var app = await DashboardApp.StartAsync(env);
        await app.PublishStageAsync("eco", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);
        using var executor = await app.LoginAsync("executor");

        var accepted = await executor.PostAsJsonAsync("/psr/api/stages/eco/runs", new { inputs = new { texto = "oi" }, idempotencyKey = "k-1" }, Ct);
        var runId = (await accepted.JsonAsync())["runId"]!.GetValue<string>();
        var cancel = await executor.PostAsync($"/psr/api/runs/{runId}/cancel", null, Ct);
        var again = await executor.PostAsync($"/psr/api/runs/{runId}/cancel", null, Ct);

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        cancel.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.JsonAsync())["code"]!.GetValue<string>().ShouldBe("RUN_NOT_CANCELLABLE");
        (await env.ScalarAsync<string>($"SELECT triggered_by || ' ' || trigger || ' ' || status FROM psr.runs WHERE id = '{runId}'"))
            .ShouldBe("login:executor Dashboard Cancelled");
        var audit = await env.ColumnAsync<string>($"SELECT action || ' ' || actor FROM psr.audit_events WHERE entity_id = '{runId}' ORDER BY occurred_at, id");
        audit.ShouldBe(["RunTriggered login:executor", "RunCancelled login:executor"], ignoreOrder: true);
    }

    [Fact]
    public async Task Spec007_AC25_Overview_CountsTheRunsOfBothDaysFailuresAndNextSchedules()
    {
        // The simulated clock only moves forward: it starts 50 h ago and walks to "now".
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now.AddHours(-50));
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python, time);
        await env.ScalarAsync<int>("SELECT psr.ensure_partitions(now() - interval '14 days', 4, 0, 0)");
        await using var app = await DashboardApp.StartAsync(env);
        await app.PublishStageAsync("eco", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);
        var queue = app.Services.GetRequiredService<IRunQueue>();

        var outside = (await queue.EnqueueStageAsync("eco", new { texto = "fora das 48 h" }, cancellationToken: Ct)).RunId;
        time.SetUtcNow(now.AddHours(-36));
        var older = new List<RunId>();
        for (var i = 0; i < 3; i++)
        {
            older.Add((await queue.EnqueueStageAsync("eco", new { texto = "antes" }, cancellationToken: Ct)).RunId);
        }

        time.SetUtcNow(now.AddHours(-2));
        var recent = new List<RunId>();
        for (var i = 0; i < 4; i++)
        {
            recent.Add((await queue.EnqueueStageAsync("eco", new { texto = "agora" }, cancellationToken: Ct)).RunId);
        }

        time.SetUtcNow(now);
        await app.Runs.CancelAsync(recent[0], PsrEnvironment.Author, Ct);
        await env.ScalarAsync<int>($$"""
            WITH u AS (
                UPDATE psr.runs SET status = 'Failed', finished_at = created_at + interval '1 minute',
                       error = '{"kind":"ScriptError","message":"falhou","code":"X"}'
                WHERE id IN ('{{recent[1]}}', '{{older[0]}}') RETURNING 1)
            SELECT count(*)::int FROM u
            """);
        var pipeline = await app.Admin.CreatePipelineAsync(new NewCatalogItem("trilha", "Trilha"), PsrEnvironment.Author, Ct);
        var saved = await app.Admin.SavePipelineDraftAsync("trilha", pipeline.Version, Scripts.SingleStep("eco", 1, [Scripts.Text("texto")]),
            pipeline.RowVersion, PsrEnvironment.Author, Ct);
        await app.Admin.PublishPipelineAsync("trilha", saved.Version, saved.RowVersion, PsrEnvironment.Author, Ct);
        var schedule = await app.Admin.CreateScheduleAsync("trilha", new ScheduleData
        {
            Name = "De hora em hora",
            Cron = "0 * * * *",
            Inputs = new JsonObject { ["texto"] = "agendado" },
        }, PsrEnvironment.Author, Ct);
        using var reader = await app.LoginAsync("leitor");

        var overview = await (await reader.GetAsync("/psr/api/overview", Ct)).JsonAsync();

        overview["last24Hours"]!["Queued"]!.GetValue<int>().ShouldBe(2);
        overview["last24Hours"]!["Cancelled"]!.GetValue<int>().ShouldBe(1);
        overview["last24Hours"]!["Failed"]!.GetValue<int>().ShouldBe(1);
        overview["last24Hours"]!["Succeeded"]!.GetValue<int>().ShouldBe(0);
        overview["previous24Hours"]!["Queued"]!.GetValue<int>().ShouldBe(2);
        overview["previous24Hours"]!["Failed"]!.GetValue<int>().ShouldBe(1);
        overview["inProgress"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()).ShouldBe(
            [recent[3].ToString(), recent[2].ToString(), older[2].ToString(), older[1].ToString(), outside.ToString()], ignoreOrder: true);
        overview["recentFailures"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()).ShouldBe([recent[1].ToString(), older[0].ToString()]);
        overview["recentFailures"]![0]!["error"]!["message"]!.GetValue<string>().ShouldBe("falhou");
        var next = overview["upcomingSchedules"]!.AsArray().Single()!;
        next["id"]!.GetValue<string>().ShouldBe(schedule.Id.ToString());
        next["pipelineKey"]!.GetValue<string>().ShouldBe("trilha");
        next["nextRunAt"]!.GetValue<DateTimeOffset>().ShouldBe(schedule.NextRunAt!.Value);
    }
}
