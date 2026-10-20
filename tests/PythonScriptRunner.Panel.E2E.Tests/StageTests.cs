using System.Diagnostics;
using System.Text.RegularExpressions;
using PythonScriptRunner.Panel.E2E.Tests.Infrastructure;

namespace PythonScriptRunner.Panel.E2E.Tests;

/// <summary>Spec 008 §2: the stage editor (Monaco, fields, test through the queue, publish, conflicts) and the stage list.</summary>
public sealed class StageTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python, BrowserFixture browser)
    : PanelTest(postgres, broker, python, browser)
{
    [Fact]
    public async Task Spec008_AC01_Author_CreatesWritesTestsAndPublishesTheStageEco()
    {
        await StartAsync();
        var page = await OpenAsync(PanelUsers.Author);
        await GoToAsync(page, "etapas");
        await page.GetByTestId("create").ClickAsync();
        await page.GetByTestId("new-key").Locator("input").FillAsync("eco");
        await page.GetByTestId("new-name").Locator("input").FillAsync("Eco");
        await page.GetByTestId("confirm-create").ClickAsync();
        await Expect(page).ToHaveURLAsync(App.UrlOf("etapas/eco/editar"));

        await page.GetByTestId("code-editor").First.SetCodeAsync(PanelScripts.Echo);
        await AddFieldAsync(page, "inputs", "texto");
        await AddFieldAsync(page, "outputs", "texto");
        await page.GetByTestId("open-test").ClickAsync();
        var test = page.GetByTestId("stage-test");
        await test.Locator("[data-field=texto]").GetByRole(AriaRole.Textbox).FillAsync("olá");
        await test.GetByTestId("run-test").ClickAsync();

        await Expect(test.GetByTestId("test-status")).ToHaveAttributeAsync("data-status", "Succeeded", new LocatorAssertionsToHaveAttributeOptions { Timeout = 120_000 });
        await Expect(test.Locator("[data-testid=test-outputs] [data-output=texto]")).ToContainTextAsync("olá");
        await Expect(test.GetByTestId("test-stdout")).ToContainTextAsync("eco: olá");

        await page.GetByTestId("publish").ClickAsync();
        var published = page.GetByTestId("published");
        await Expect(published).ToContainTextAsync("Versão 1 publicada");
        await published.GetByRole(AriaRole.Button).ClickAsync();
        await Expect(page).ToHaveURLAsync(App.UrlOf("etapas/eco"));
        await Expect(page.GetByTestId("versions-table")).ToContainTextAsync("Publicada");
        var stage = await App.Host.Admin.GetStageAsync("eco", Ct);
        stage!.LatestPublishedVersion.ShouldBe(1);
        (await App.Host.Admin.GetStageVersionAsync("eco", 1, Ct))!.Data.Script.ShouldBe(PanelScripts.Echo.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Spec008_AC02_SyntaxError_ShowsLineAndColumn_AndTheStageStaysADraft()
    {
        await StartAsync();
        await App.SaveStageDraftAsync("quebrada", PanelScripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);
        var page = await OpenAsync(PanelUsers.Author);
        await GoToAsync(page, "etapas/quebrada/editar");

        await page.GetByTestId("code-editor").First.SetCodeAsync("""
            from psrunner import stage


            @stage
            def run(inputs, ctx)
                return {"texto": inputs["texto"]}
            """);
        await page.GetByTestId("publish").ClickAsync();

        await Expect(page.GetByTestId("publish-error")).ToContainTextAsync(new Regex(@"Erro de sintaxe na linha 5, coluna \d+"));
        await Expect(page.Locator(".monaco-editor .squiggly-error")).Not.ToHaveCountAsync(0);
        await Expect(page.GetByTestId("published")).ToHaveCountAsync(0);
        (await App.Host.Admin.GetStageAsync("quebrada", Ct))!.LatestPublishedVersion.ShouldBeNull();
        (await App.Host.Admin.GetStageDraftAsync("quebrada", Ct))!.Data.Script.ShouldContain("def run(inputs, ctx)\n");
    }

    [Fact]
    public async Task Spec008_AC09_TwoBrowsersEditTheSameDraft_TheSecondSaveOffersReloadAndOverwrite()
    {
        await StartAsync();
        await App.SaveStageDraftAsync("eco", PanelScripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);
        var first = await OpenAsync(PanelUsers.Author);
        var second = await OpenAsync(PanelUsers.Admin);
        foreach (var page in new[] { first, second })
        {
            await GoToAsync(page, "etapas/eco/editar");
            await Expect(page.GetByTestId("save")).ToBeVisibleAsync();
        }

        await AddFieldAsync(first, "inputs", "primeiro");
        await first.GetByTestId("save").ClickAsync();
        await Expect(first.GetByTestId("dirty")).ToBeHiddenAsync();
        await AddFieldAsync(second, "inputs", "segundo");
        await second.GetByTestId("save").ClickAsync();

        var conflict = second.GetByTestId("conflict-dialog");
        await Expect(conflict).ToBeVisibleAsync();
        await Expect(conflict).ToContainTextAsync("autor");
        await Expect(conflict.GetByTestId("conflict-reload")).ToHaveTextAsync("Recarregar");
        await Expect(conflict.GetByTestId("conflict-overwrite")).ToHaveTextAsync("Sobrescrever");
        await conflict.GetByTestId("conflict-overwrite").ClickAsync();

        await Expect(second.GetByTestId("dirty")).ToBeHiddenAsync();
        var draft = await App.Host.Admin.GetStageDraftAsync("eco", Ct);
        draft!.Data.Inputs.Select(f => f.Name).ShouldBe(["texto", "segundo"]);
        draft.Authorship.UpdatedBy.ShouldBe("login:admin");
    }

    [Fact]
    public async Task Spec008_AC23_WithoutWorkers_TheTestIsQueued_AndWarnsAfter15Seconds()
    {
        await StartAsync(s => s.Worker = false);
        await App.SaveStageDraftAsync("eco", PanelScripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);
        var page = await OpenAsync(PanelUsers.Author);
        await GoToAsync(page, "etapas/eco/editar");
        await page.GetByTestId("open-test").ClickAsync();
        var test = page.GetByTestId("stage-test");
        await test.Locator("[data-field=texto]").GetByRole(AriaRole.Textbox).FillAsync("olá");
        var clock = Stopwatch.StartNew();
        await test.GetByTestId("run-test").ClickAsync();

        var status = test.GetByTestId("test-status");
        await Expect(status).ToHaveAttributeAsync("data-status", "Queued");
        await Expect(status).ToHaveTextAsync("Na fila");
        await Expect(test.GetByTestId("no-worker")).ToHaveCountAsync(0);
        await Expect(test.GetByTestId("no-worker")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        clock.Elapsed.ShouldBeGreaterThan(TimeSpan.FromSeconds(14));
        await Expect(status).ToHaveAttributeAsync("data-status", "Queued");
    }

    [Fact]
    public async Task Spec008_AC25_StageList_CountsOnlyThePipelinesWithAPublishedVersion()
    {
        await StartAsync(s => s.Worker = false);
        await App.ImportFixtureAsync("calculo-progressao");
        await App.SavePipelineDraftAsync("rascunho", new PipelineDraftData
        {
            Inputs = [Scripts.Number("a1"), Scripts.Number("razao"), Scripts.Integer("n")],
            Steps = [new PipelineStepDefinition("termos", "progressao-termos", 1)],
            Outputs = [new PipelineOutputDefinition("termos", new StepOutputSource("termos", "termos"))],
        });
        var page = await OpenAsync(PanelUsers.Reader);

        await GoToAsync(page, "etapas");

        var row = page.Locator("tr").Filter(new LocatorFilterOptions { Has = page.Locator("[data-key=progressao-termos]") });
        await Expect(row.GetByTestId("used-by")).ToHaveTextAsync("usada em 1 trilha");
    }

    /// <summary>Adds a field (string, required) to the inputs or outputs builder and names it.</summary>
    private static async Task AddFieldAsync(IPage page, string kind, string name)
    {
        var builder = page.Locator($"[data-testid=field-builder][data-kind={kind}]");
        await builder.GetByTestId("add-field").ClickAsync();
        var panel = builder.Locator(".v-expansion-panel").Last;
        await panel.Locator(".v-expansion-panel-title").ClickAsync();
        await panel.GetByLabel("Nome", new LocatorGetByLabelOptions { Exact = true }).FillAsync(name);
        await Expect(builder.Locator($"[data-field={name}]")).ToBeVisibleAsync();
    }
}
