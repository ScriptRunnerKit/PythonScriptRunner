using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using PythonScriptRunner.Tests.Infrastructure;

namespace PythonScriptRunner.AppHost.Tests;

/// <summary>
/// Spec 009, AC-1 and AC-2: the Aspire AppHost brings the whole environment up, and the fixed user <c>autor</c> imports the
/// fixtures and runs CS-1 and CS-2 through the dashboard API, with the e-mail arriving in Mailpit.
/// </summary>
public sealed class AppHostTests
{
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
        using var http = await PanelApi.LoginAsync(app.GetEndpoint("dashboard", "http"));
        var smtp = app.GetEndpoint("mailpit", "smtp");
        await PanelApi.ImportAndPublishAsync(http, "calculo-progressao");
        var runsCs2 = !OperatingSystem.IsWindows();
        if (runsCs2)
        {
            await PanelApi.ImportAndPublishAsync(http, "html-para-email", (path, content) => path.EndsWith(".pipeline.json", StringComparison.Ordinal)
                ? content.Replace("smtp.exemplo.com", smtp.Host, StringComparison.Ordinal)
                    .Replace("\"const\": 465", $"\"const\": {smtp.Port}", StringComparison.Ordinal)
                : content);
        }

        var cs1 = await PanelApi.RunAsync(http, "calculo-progressao", JsonContent.Create(new
        {
            inputs = new { a1 = 1, razao = 2, n = 3, coeficientes = new[] { 0, 1 } },
        }));
        (await PanelApi.WaitForSuccessAsync(http, cs1))["outputs"]!["soma"]!.GetValue<double>().ShouldBe(9);

        // CS-2 needs weasyprint (GTK/pango), which runs on Linux only (D-106).
        if (runsCs2)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("""{"destinatarios": ["financeiro@exemplo.com"], "assunto": "Relatório do Aspire"}"""), "inputs");
            var html = new ByteArrayContent(Encoding.UTF8.GetBytes("<html><body><h1>Relatório</h1></body></html>"));
            html.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            form.Add(html, "html", "pagina.html");
            await PanelApi.WaitForSuccessAsync(http, await PanelApi.RunAsync(http, "html-para-email", form));

            using var mailpit = app.CreateHttpClient("mailpit", "http");
            var messages = (await mailpit.GetFromJsonAsync<JsonObject>("/api/v1/messages", Ct))!["messages"]!.AsArray();
            messages.Select(m => m!["Subject"]!.GetValue<string>()).ShouldContain("Relatório do Aspire");
        }
    }
}
