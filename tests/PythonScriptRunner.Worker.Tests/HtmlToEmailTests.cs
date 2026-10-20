using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;

namespace PythonScriptRunner.Worker.Tests;

/// <summary>Spec 006, AC-2 (CS-2): HTML → PDF → e-mail, queued by the library, with Mailpit as the SMTP server.</summary>
public sealed class HtmlToEmailTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python)
{
    private const ushort SmtpPort = 1025;
    private const ushort ApiPort = 8025;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spec006_AC02_HtmlToEmail_SendsThePdfAndKeepsItDownloadableForSevenDays()
    {
        // weasyprint needs pango (GTK) at runtime: the CI runs this test on Linux (D-106).
        Assert.SkipWhen(OperatingSystem.IsWindows(), "weasyprint needs GTK/pango; CS-2 runs on Linux (D-106).");

        await using var mailpit = new ContainerBuilder("axllent/mailpit:v1.27.0")
            .WithEnvironment("MP_SMTP_AUTH_ACCEPT_ANY", "1")
            .WithEnvironment("MP_SMTP_AUTH_ALLOW_INSECURE", "1")
            .WithPortBinding(SmtpPort, true)
            .WithPortBinding(ApiPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(ApiPort).ForPath("/livez")))
            .Build();
        await mailpit.StartAsync(Ct);
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var host = await env.StartHostAsync(s =>
        {
            s.Configuration["PythonScriptRunner:Secrets:SMTP_USUARIO"] = "psr";
            s.Configuration["PythonScriptRunner:Secrets:SMTP_SENHA"] = "segredo-de-teste";
        });
        await env.ImportFixtureAsync(host, "html-para-email", (path, content) => path.EndsWith(".pipeline.json", StringComparison.Ordinal)
            ? content.Replace("smtp.exemplo.com", mailpit.Hostname, StringComparison.Ordinal)
                .Replace("\"const\": 465", $"\"const\": {mailpit.GetMappedPublicPort(SmtpPort)}", StringComparison.Ordinal)
            : content);
        var html = PsrFile.FromText("<html><body><h1>Relatório</h1><p>Gerado pelo PythonScriptRunner.</p></body></html>", "pagina.html", "text/html");

        var handle = await host.Queue.EnqueuePipelineAsync("html-para-email", new
        {
            html,
            destinatarios = new[] { "financeiro@exemplo.com" },
            assunto = "Relatório mensal",
        }, cancellationToken: Ct);
        var run = await host.WaitForRunAsync(handle.RunId, timeout: TimeSpan.FromMinutes(5));

        run.Status.ShouldBe(RunStatus.Succeeded, run.Error?.Message);
        run.Outputs!["paginas"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(1);
        var pdf = run.Artifacts.Single(a => a.Direction == ArtifactDirection.Output && a.FieldName == "pdf");
        pdf.ContentType.ShouldBe("application/pdf");
        (pdf.ContentExpiresAt - run.CreatedAt).ShouldBe(TimeSpan.FromDays(7), TimeSpan.FromMinutes(5));
        await using (var content = await host.Artifacts.OpenReadAsync(pdf.Id, Ct))
        {
            var header = new byte[4];
            await content.ReadExactlyAsync(header, Ct);
            System.Text.Encoding.ASCII.GetString(header).ShouldBe("%PDF");
        }

        using var http = new HttpClient { BaseAddress = new Uri($"http://{mailpit.Hostname}:{mailpit.GetMappedPublicPort(ApiPort)}") };
        var messages = (await http.GetFromJsonAsync<JsonObject>("/api/v1/messages", Ct))!["messages"]!.AsArray();
        var message = messages.Single()!;
        message["Subject"]!.GetValue<string>().ShouldBe("Relatório mensal");
        var detail = (await http.GetFromJsonAsync<JsonObject>($"/api/v1/message/{message["ID"]!.GetValue<string>()}", Ct))!;
        var attachment = detail["Attachments"]!.AsArray().Single()!;
        attachment["FileName"]!.GetValue<string>().ShouldBe("relatorio.pdf");
        attachment["ContentType"]!.GetValue<string>().ShouldBe("application/pdf");
        run.Outputs!["message_id"]!.GetValue<string>().ShouldBe($"<{message["MessageID"]!.GetValue<string>()}>");
    }
}
