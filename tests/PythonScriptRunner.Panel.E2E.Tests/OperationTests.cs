using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PythonScriptRunner.Panel.E2E.Tests.Infrastructure;

namespace PythonScriptRunner.Panel.E2E.Tests;

/// <summary>Spec 008: running pipelines and following the runs live (flow, reprocess, cancel, polling) and the schedule form.</summary>
public sealed partial class OperationTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python, BrowserFixture browser)
    : PanelTest(postgres, broker, python, browser)
{
    /// <summary>Records every <c>data-status</c> a step node of the flow shows, without reloading the page.</summary>
    private const string RecordStepStatuses = """
        window.__psrStatuses = {};
        new MutationObserver(() => {
            for (const node of document.querySelectorAll('[data-testid=run-flow] [data-step][data-status]')) {
                const seen = (window.__psrStatuses[node.dataset.step] ??= []);
                if (seen.at(-1) !== node.dataset.status) seen.push(node.dataset.status);
            }
        }).observe(document, { subtree: true, childList: true, attributes: true, attributeFilter: ['data-status'] });
        """;

    [Fact]
    public async Task Spec008_AC06_Executor_RunsCalculoProgressao_AndSeesTheNodesChangeLiveUntilSucceeded()
    {
        await StartAsync(s => s.Worker = false);
        await App.ImportFixtureAsync("calculo-progressao");
        var page = await OpenAsync(PanelUsers.Executor);
        await page.Context.AddInitScriptAsync(RecordStepStatuses);
        await GoToAsync(page, "trilhas/calculo-progressao");
        await page.GetByTestId("run-pipeline").ClickAsync();
        var dialog = page.GetByTestId("run-dialog");
        await dialog.FillCalculoAsync();
        await dialog.GetByTestId("submit-run").ClickAsync();

        await Expect(page).ToHaveURLAsync(RunUrl());
        var flow = page.GetByTestId("run-flow");
        await Expect(flow.Locator("[data-step]")).ToHaveCountAsync(4);
        await Expect(flow.Locator("[data-step][data-status=Pending], [data-step][data-status=Queued]")).ToHaveCountAsync(4);

        // The worker starts only once the page shows the run waiting: on a fast machine the first step could otherwise
        // finish before the page loads, and its node would only ever be seen as Succeeded.
        await App.Environment.StartHostAsync();
        await Expect(page.GetByTestId("run-status")).ToHaveAttributeAsync("data-status", "Succeeded", new LocatorAssertionsToHaveAttributeOptions { Timeout = 180_000 });
        await Expect(flow.Locator("[data-step][data-status=Succeeded]")).ToHaveCountAsync(4);

        var seen = JsonSerializer.Deserialize<Dictionary<string, string[]>>(await page.EvaluateAsync<string>("() => JSON.stringify(window.__psrStatuses)"))!;
        seen.Keys.ShouldBe(["termos", "polinomio", "estatisticas", "tabela"], ignoreOrder: true);
        seen.Values.ShouldAllBe(statuses => statuses.Length >= 2 && statuses.Last() == "Succeeded");
        await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Saídas" }).ClickAsync();
        await Expect(page.Locator("[data-testid=run-outputs] [data-output=soma]")).ToContainTextAsync("9");
        await Expect(page.Locator("[data-testid=run-outputs] [data-output=media]")).ToContainTextAsync("3");
        await Expect(page.Locator("[data-testid=run-outputs] [data-output=tabela]")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Spec008_AC07_Reprocess_KeepsTheFinishedStep_AndTheFailedStepShowsAttempt2()
    {
        await StartAsync();
        await App.PublishStageAsync("eco", PanelScripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);
        await App.PublishStageAsync("falha-uma-vez", PanelScripts.FailOnce, [Scripts.Text("marcador")], [Scripts.Text("texto")]);
        await App.PublishPipelineAsync("retoma", new PipelineDraftData
        {
            Inputs = [Scripts.Text("texto"), Scripts.Text("marcador")],
            Steps =
            [
                new PipelineStepDefinition("eco", "eco", 1),
                new PipelineStepDefinition("falha", "falha-uma-vez", 1) { Bindings = [new InputBinding("marcador", new PipelineInputSource("marcador"))] },
            ],
            Outputs = [new PipelineOutputDefinition("texto", new StepOutputSource("falha", "texto"))],
        });
        Directory.CreateDirectory(App.Environment.WorkRoot);
        var marker = Path.Combine(App.Environment.WorkRoot, "marcador.txt");
        var handle = await App.Host.Queue.EnqueuePipelineAsync("retoma", new { texto = "olá", marcador = marker }, cancellationToken: Ct);
        (await App.Host.WaitForRunAsync(handle.RunId)).Status.ShouldBe(RunStatus.Failed);
        var page = await OpenAsync(PanelUsers.Executor);
        await GoToAsync(page, $"execucoes/{handle.RunId}");
        var flow = page.GetByTestId("run-flow");
        await Expect(flow.Locator("[data-step=falha]")).ToHaveAttributeAsync("data-status", "Failed");

        await page.GetByTestId("reprocess-run").ClickAsync();
        await page.GetByTestId("confirm").ClickAsync();

        await Expect(page.GetByTestId("run-status")).ToHaveAttributeAsync("data-status", "Succeeded", new LocatorAssertionsToHaveAttributeOptions { Timeout = 60_000 });
        await Expect(flow.Locator("[data-step=falha]")).ToHaveAttributeAsync("data-status", "Succeeded");
        await Expect(flow.Locator("[data-step=falha]")).ToContainTextAsync("#2");
        await Expect(flow.Locator("[data-step=eco]")).ToHaveAttributeAsync("data-status", "Succeeded");
        await Expect(flow.Locator("[data-step=eco]")).Not.ToContainTextAsync("#");
        await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Passos" }).ClickAsync();
        await Expect(page.Locator("[data-testid=run-steps] tr[data-attempt]")).ToHaveCountAsync(3);
    }

    [Fact]
    public async Task Spec008_AC08_CancellingARunningRun_ShowsCancelledInTheFlowWithin3Seconds()
    {
        await StartAsync();
        await PublishSleepAsync();
        var handle = await App.Host.Queue.EnqueuePipelineAsync("demorada", new { segundos = 120, texto = "x" }, cancellationToken: Ct);
        await App.Host.WaitForRunAsync(handle.RunId, r => r.Steps[0].Status == StepStatus.Running);
        var page = await OpenAsync(PanelUsers.Executor);
        await GoToAsync(page, $"execucoes/{handle.RunId}");
        var node = page.Locator("[data-testid=run-flow] [data-step=passo]");
        await Expect(node).ToHaveAttributeAsync("data-status", "Running");

        await page.GetByTestId("cancel-run").ClickAsync();
        var clock = Stopwatch.StartNew();
        await page.GetByTestId("confirm").ClickAsync();

        await Expect(node).ToHaveAttributeAsync("data-status", "Cancelled", new LocatorAssertionsToHaveAttributeOptions { Timeout = 3_000 });
        clock.Elapsed.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(3));
        await Expect(page.GetByTestId("run-status")).ToHaveAttributeAsync("data-status", "Cancelled");
    }

    [Fact]
    public async Task Spec008_AC11_ScheduleForm_PreviewsFiveOccurrencesAt2InSaoPaulo_AndFileInputsCannotBeScheduled()
    {
        await StartAsync(s => s.Worker = false);
        await App.ImportFixtureAsync("calculo-progressao");
        await App.PublishStageAsync("recebe-arquivo", PanelScripts.Echo, [Scripts.File("arquivo")], [Scripts.Text("texto")]);
        await App.PublishPipelineAsync("com-arquivo", Scripts.SingleStep("recebe-arquivo", 1, [Scripts.File("arquivo")]));
        var page = await OpenAsync(PanelUsers.Author);

        var form = await OpenScheduleFormAsync(page, "calculo-progressao");
        await form.GetByTestId("cron").Locator("input").FillAsync("0 2 * * *");
        var preview = form.GetByTestId("schedule-preview").Locator(".v-list-item-title");
        await Expect(preview).ToHaveCountAsync(5);
        var occurrences = await preview.AllInnerTextsAsync();
        occurrences.ShouldAllBe(text => Occurrence().IsMatch(text.Trim()));
        occurrences.Distinct().Count().ShouldBe(5);
        await Expect(form.GetByTestId("schedule-file-input")).ToHaveCountAsync(0);

        form = await OpenScheduleFormAsync(page, "com-arquivo");
        await Expect(form.GetByTestId("schedule-file-input")).ToBeVisibleAsync();
        await Expect(form.GetByTestId("save-schedule")).ToBeDisabledAsync();
    }

    [Fact]
    public async Task Spec008_AC13_WithoutTheHub_ShowsTheBanner_AndTheScreenKeepsUpdatingByPolling()
    {
        await StartAsync();
        await PublishSleepAsync();
        var page = await OpenAsync(PanelUsers.Reader);
        await page.RouteAsync("**/psr/hubs/**", route => route.AbortAsync());
        var handle = await App.Host.Queue.EnqueuePipelineAsync("demorada", new { segundos = 8, texto = "x" }, cancellationToken: Ct);
        await App.Host.WaitForRunAsync(handle.RunId, r => r.Steps[0].Status == StepStatus.Running);

        await GoToAsync(page, $"execucoes/{handle.RunId}");

        await Expect(page.GetByTestId("realtime-banner")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("run-status")).ToHaveAttributeAsync("data-status", "Running");
        await Expect(page.GetByTestId("run-status")).ToHaveAttributeAsync("data-status", "Succeeded", new LocatorAssertionsToHaveAttributeOptions { Timeout = 40_000 });
        await Expect(page.GetByTestId("realtime-banner")).ToBeVisibleAsync();
    }

    private async Task PublishSleepAsync()
    {
        IReadOnlyList<FieldDefinition> inputs = [Scripts.Number("segundos"), Scripts.Text("texto")];
        await App.PublishStageAsync("dorme", Scripts.Sleep, inputs, [Scripts.Text("texto")]);
        await App.PublishPipelineAsync("demorada", Scripts.SingleStep("dorme", 1, inputs));
    }

    private async Task<ILocator> OpenScheduleFormAsync(IPage page, string pipeline)
    {
        await GoToAsync(page, $"trilhas/{pipeline}");
        await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Agendamentos" }).ClickAsync();
        await page.GetByTestId("create-schedule").ClickAsync();
        var form = page.GetByTestId("schedule-form");
        await Expect(form).ToBeVisibleAsync();
        return form;
    }

    private static Regex RunUrl() => new("/psr/execucoes/[0-9a-f-]{36}$");

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} 02:00(:00)? \(-03:00\)$")]
    private static partial Regex Occurrence();
}
