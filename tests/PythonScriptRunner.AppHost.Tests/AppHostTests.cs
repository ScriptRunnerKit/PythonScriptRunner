using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace PythonScriptRunner.AppHost.Tests;

/// <summary>
/// Spec 009, AC-1 and AC-2: the Aspire AppHost brings the whole environment up, and the fixed user <c>autor</c> imports the
/// fixtures and runs CS-1 and CS-2 through the dashboard API, with the e-mail arriving in Mailpit.
/// </summary>
public sealed class AppHostTests
{
    private const string DevPassword = "psr-dev";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spec009_AC01_AC02_TheAppHostBringsEverythingUp_AndTheAuthorRunsCs1AndCs2()
    {
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.PythonScriptRunner_AppHost>(
            [], (options, _) => options.DisableDashboard = false, Ct);

        // A fresh database each run: the persistent volume is for developers, not for tests.
        foreach (var container in appHost.Resources.OfType<ContainerResource>())
        {
            foreach (var volume in container.Annotations.OfType<ContainerMountAnnotation>().Where(m => m.Type == ContainerMountType.Volume).ToList())
            {
                container.Annotations.Remove(volume);
            }
        }

        await using var app = await appHost.BuildAsync(Ct);
        await app.StartAsync(Ct);

        // AC-1: every resource is up, and the hosts send their telemetry to the Aspire dashboard.
        using (var start = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            start.CancelAfter(StartTimeout);
            await Task.WhenAll(
                app.ResourceNotifications.WaitForResourceHealthyAsync("worker", start.Token),
                app.ResourceNotifications.WaitForResourceHealthyAsync("dashboard", start.Token),
                app.ResourceNotifications.WaitForResourceAsync("mailpit", KnownResourceStates.Running, start.Token),
                app.ResourceNotifications.WaitForResourceAsync("panel", KnownResourceStates.Running, start.Token));
        }

        foreach (var name in new[] { "worker", "dashboard" })
        {
            var running = await app.ResourceNotifications.WaitForResourceAsync(name, e => !e.Snapshot.EnvironmentVariables.IsDefaultOrEmpty, Ct);
            running.Snapshot.EnvironmentVariables.Select(v => v.Name).ShouldContain("OTEL_EXPORTER_OTLP_ENDPOINT", $"{name} sends telemetry to the dashboard");
        }

        // AC-2: the fixed user autor imports the fixtures, publishes them and runs CS-1 and CS-2.
        var dashboard = app.GetEndpoint("dashboard", "http");
        using var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = dashboard };
        http.DefaultRequestHeaders.Add("X-PSR-Request", "1");
        (await http.PostAsJsonAsync("/psr/api/auth/login", new { name = "autor", password = DevPassword }, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        var smtp = app.GetEndpoint("mailpit", "smtp");
        await ImportAndPublishAsync(http, "calculo-progressao", static (_, content) => content);
        var runsCs2 = !OperatingSystem.IsWindows();
        if (runsCs2)
        {
            await ImportAndPublishAsync(http, "html-para-email", (path, content) => path.EndsWith(".pipeline.json", StringComparison.Ordinal)
                ? content.Replace("smtp.exemplo.com", smtp.Host, StringComparison.Ordinal)
                    .Replace("\"const\": 465", $"\"const\": {smtp.Port}", StringComparison.Ordinal)
                : content);
        }

        var cs1 = await RunAsync(http, "calculo-progressao", JsonContent.Create(new
        {
            inputs = new { a1 = 1, razao = 2, n = 3, coeficientes = new[] { 0, 1 } },
        }));
        (await WaitForRunAsync(http, cs1))["outputs"]!["soma"]!.GetValue<double>().ShouldBe(9);

        // CS-2 needs weasyprint (GTK/pango), which runs on Linux only (D-106).
        if (runsCs2)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("""{"destinatarios": ["financeiro@exemplo.com"], "assunto": "Relatório do Aspire"}"""), "inputs");
            var html = new ByteArrayContent(Encoding.UTF8.GetBytes("<html><body><h1>Relatório</h1></body></html>"));
            html.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            form.Add(html, "html", "pagina.html");
            var cs2 = await RunAsync(http, "html-para-email", form);
            await WaitForRunAsync(http, cs2);

            using var mailpit = app.CreateHttpClient("mailpit", "http");
            var messages = (await mailpit.GetFromJsonAsync<JsonObject>("/api/v1/messages", Ct))!["messages"]!.AsArray();
            messages.Select(m => m!["Subject"]!.GetValue<string>()).ShouldContain("Relatório do Aspire");
        }
    }

    /// <summary>Imports a fixture as a zip (drafts), then publishes its stages and pipelines with their ETags.</summary>
    private static async Task ImportAndPublishAsync(HttpClient http, string fixture, Func<string, string, string> transform)
    {
        var root = Path.Combine(RepositoryRoot(), "tests", "fixtures", fixture);
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => !f.Contains("__pycache__", StringComparison.Ordinal)))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                await using var writer = new StreamWriter(archive.CreateEntry(relative).Open());
                await writer.WriteAsync(transform(relative, await File.ReadAllTextAsync(file, Ct)));
            }
        }

        using var form = new MultipartFormDataContent();
        var archiveContent = new ByteArrayContent(zip.ToArray());
        archiveContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(archiveContent, "archive", $"{fixture}.zip");
        var import = await http.PostAsync("/psr/api/import", form, Ct);
        import.StatusCode.ShouldBe(HttpStatusCode.OK, await import.Content.ReadAsStringAsync(Ct));
        var items = (await import.Content.ReadFromJsonAsync<JsonObject>(Ct))!["items"]!.AsArray();

        // Stages first: the pipeline references their published versions.
        foreach (var item in items.OrderBy(i => i!["kind"]!.GetValue<string>() == "Stage" ? 0 : 1))
        {
            var kind = item!["kind"]!.GetValue<string>() == "Stage" ? "stages" : "pipelines";
            var key = item["key"]!.GetValue<string>();
            var draft = await http.GetAsync($"/psr/api/{kind}/{key}/draft", Ct);
            using var publish = new HttpRequestMessage(HttpMethod.Post, $"/psr/api/{kind}/{key}/draft/publish");
            publish.Headers.IfMatch.Add(draft.Headers.ETag!);
            var published = await http.SendAsync(publish, Ct);
            published.StatusCode.ShouldBe(HttpStatusCode.OK, await published.Content.ReadAsStringAsync(Ct));
        }
    }

    private static async Task<string> RunAsync(HttpClient http, string pipeline, HttpContent body)
    {
        var response = await http.PostAsync($"/psr/api/pipelines/{pipeline}/runs", body, Ct);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["runId"]!.GetValue<string>();
    }

    private static async Task<JsonObject> WaitForRunAsync(HttpClient http, string runId)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        while (true)
        {
            var run = (await http.GetFromJsonAsync<JsonObject>($"/psr/api/runs/{runId}", timeout.Token))!;
            var status = run["status"]!.GetValue<string>();
            if (status is "Succeeded" or "Failed" or "Cancelled" or "TimedOut")
            {
                status.ShouldBe("Succeeded", run["error"]?.ToJsonString());
                return run;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PythonScriptRunner.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (PythonScriptRunner.slnx) was not found.");
    }
}
