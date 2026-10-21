using System.Text.RegularExpressions;
using PythonScriptRunner.Panel.E2E.Tests.Infrastructure;

namespace PythonScriptRunner.Panel.E2E.Tests;

/// <summary>Spec 008 §3: the visual pipeline editor (palette, typed handles, linear rule, outputs, test and publish).</summary>
public sealed class PipelineEditorTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python, BrowserFixture browser)
    : PanelTest(postgres, broker, python, browser)
{
    [Fact]
    public async Task Spec008_AC03_Author_DragsFourStagesAndConnectsTheHandles_ThenPublishesVersion1()
    {
        await StartAsync(s => s.Worker = false);
        await App.ImportFixtureAsync("calculo-progressao");
        var page = await OpenAsync(PanelUsers.Author);
        await GoToAsync(page, "trilhas");
        await page.GetByTestId("create").ClickAsync();
        await page.GetByTestId("new-key").Locator("input").FillAsync("calculo-visual");
        await page.GetByTestId("new-name").Locator("input").FillAsync("Cálculo montado no editor");
        await page.GetByTestId("confirm-create").ClickAsync();
        await Expect(page).ToHaveURLAsync(App.UrlOf("trilhas/calculo-visual/editar"));

        var canvas = page.GetByTestId("pipeline-canvas");
        await Expect(canvas.Locator(".vue-flow__node")).ToHaveCountAsync(2);
        string[] stages = ["progressao-termos", "polinomio-avaliar", "estatisticas", "montar-tabela"];
        for (var i = 0; i < stages.Length; i++)
        {
            await page.GetByTestId("palette").Locator($"[data-stage={stages[i]}]").DragToAsync(canvas, new LocatorDragToOptions
            {
                TargetPosition = new TargetPosition { X = 200 + (150 * i), Y = 120 + (90 * i) },
            });
            await Expect(Step(canvas, stages[i])).ToBeVisibleAsync();
        }

        await Expect(page.GetByTestId("order-bar").Locator("[data-order]")).ToHaveTextAsync(stages.Select((s, i) => $"{i + 1}. {s}").ToArray());
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Organizar" }).ClickAsync();

        // The pipeline inputs (side panel of the Inputs node); the first step takes a1, razao and n by name.
        await canvas.Locator("[data-node=inputs]").ClickAsync();
        var inputs = page.Locator("[data-testid=field-builder][data-kind=inputs]");
        await AddFieldAsync(inputs, "a1", "number");
        await AddFieldAsync(inputs, "razao", "number");
        await AddFieldAsync(inputs, "n", "integer");
        await AddFieldAsync(inputs, "coeficientes", "list", "number");

        await page.ConnectAsync(Out(canvas.Locator("[data-node=inputs]"), "coeficientes"), In(Step(canvas, "polinomio-avaliar"), "coeficientes"));
        await page.ConnectAsync(Out(Step(canvas, "progressao-termos"), "termos"), In(Step(canvas, "polinomio-avaliar"), "x"));
        await page.ConnectAsync(Out(Step(canvas, "progressao-termos"), "termos"), In(Step(canvas, "montar-tabela"), "x"));
        await page.ConnectAsync(Out(Step(canvas, "polinomio-avaliar"), "valores"), In(Step(canvas, "montar-tabela"), "y"));
        var newOutput = canvas.Locator("[data-node=outputs] [data-handle=new-output]");
        foreach (var output in new[] { "soma", "media", "desvio_padrao" })
        {
            await page.ConnectAsync(Out(Step(canvas, "estatisticas"), output), newOutput);
            await Expect(canvas.Locator($"[data-node=outputs] [data-field={output}]")).ToBeVisibleAsync();
        }

        await page.ConnectAsync(Out(Step(canvas, "montar-tabela"), "tabela"), newOutput);
        await Expect(canvas.Locator("[data-node=outputs] [data-field=tabela]")).ToBeVisibleAsync();

        await Expect(page.GetByTestId("problems")).ToHaveTextAsync("Nenhum problema encontrado.");
        await page.GetByTestId("publish").ClickAsync();
        await Expect(page.GetByTestId("published")).ToContainTextAsync("Versão 1 publicada");

        var version = await App.Host.Admin.GetPipelineVersionAsync("calculo-visual", 1, Ct);
        version!.Status.ShouldBe(VersionStatus.Published);
        var reference = (await App.Host.Admin.GetPipelineVersionAsync("calculo-progressao", 1, Ct))!.Data;
        version.Data.Steps.Select(s => s.StageKey).ShouldBe(reference.Steps.Select(s => s.StageKey));
        version.Data.Inputs.Select(f => f.Name).ShouldBe(["a1", "razao", "n", "coeficientes"]);
        version.Data.Outputs.Select(o => (o.Name, o.Source.Step, o.Source.Field)).ShouldBe(
        [
            ("soma", "estatisticas", "soma"), ("media", "estatisticas", "media"), ("desvio_padrao", "estatisticas", "desvio_padrao"),
            ("tabela", "montar-tabela", "tabela"),
        ]);
    }

    [Fact]
    public async Task Spec008_AC04_AC05_IncompatibleTypesAndLaterSteps_DisableTheTargetHandle_AndCreateNoLink()
    {
        await StartAsync(s => s.Worker = false);
        await App.ImportFixtureAsync("calculo-progressao");
        await App.PublishStageAsync("eco", PanelScripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);
        await App.SavePipelineDraftAsync("bloqueios", new PipelineDraftData
        {
            Inputs = [Scripts.Text("texto")],
            Steps =
            [
                new PipelineStepDefinition("polinomio-avaliar", "polinomio-avaliar", 1),
                new PipelineStepDefinition("estatisticas", "estatisticas", 1),
                new PipelineStepDefinition("polinomio-avaliar-2", "polinomio-avaliar", 1),
                new PipelineStepDefinition("eco", "eco", 1),
                new PipelineStepDefinition("progressao-termos", "progressao-termos", 1),
            ],
        });
        var page = await OpenAsync(PanelUsers.Author);
        await GoToAsync(page, "trilhas/bloqueios/editar");
        var canvas = page.GetByTestId("pipeline-canvas");
        await Expect(Step(canvas, "progressao-termos")).ToBeVisibleAsync();
        var statistics = In(Step(canvas, "estatisticas"), "valores");

        // An earlier step may feed a later one (the control of the AC-5 check).
        await page.BeginConnectionAsync(Out(Step(canvas, "polinomio-avaliar"), "valores"));
        await Expect(statistics).Not.ToHaveClassAsync(DisabledHandle());
        await page.Mouse.UpAsync();

        // AC-4: string output → integer input.
        await page.BeginConnectionAsync(Out(Step(canvas, "eco"), "texto"));
        var integer = In(Step(canvas, "progressao-termos"), "n");
        await Expect(integer).ToHaveClassAsync(DisabledHandle());
        await Expect(integer).ToHaveAttributeAsync("aria-disabled", "true");
        await page.EndConnectionAsync(integer);

        // AC-5: output of step 3 → input of step 2 (same type, but linear).
        await page.BeginConnectionAsync(Out(Step(canvas, "polinomio-avaliar-2"), "valores"));
        await Expect(statistics).ToHaveClassAsync(DisabledHandle());
        await page.EndConnectionAsync(statistics);

        await Expect(page.GetByTestId("dirty")).ToBeHiddenAsync();
        await Expect(Step(canvas, "progressao-termos").Locator("[data-input=n]")).Not.ToHaveClassAsync(new Regex("port--edge"));
        var draft = await App.Host.Admin.GetPipelineDraftAsync("bloqueios", Ct);
        draft!.Data.Steps.ShouldAllBe(s => s.Bindings.Count == 0);
    }

    [Fact]
    public async Task Spec008_AC16_TestingTheDraft_FollowsThePipelineTestLive_AndPublishesNothing()
    {
        await StartAsync();
        await App.ImportFixtureAsync("calculo-progressao");
        await App.Host.Admin.CreatePipelineDraftFromAsync("calculo-progressao", 1, PsrEnvironment.Author, Ct);
        var page = await OpenAsync(PanelUsers.Author);
        await GoToAsync(page, "trilhas/calculo-progressao/editar");
        await Expect(Step(page.GetByTestId("pipeline-canvas"), "termos")).ToBeVisibleAsync();

        await page.GetByTestId("open-test").ClickAsync();
        var dialog = page.GetByTestId("test-dialog");
        await dialog.FillCalculoAsync();
        await dialog.GetByTestId("run-test").ClickAsync();

        await Expect(page).ToHaveURLAsync(new Regex("/psr/execucoes/[0-9a-f-]{36}$"));
        await Expect(page.GetByText("Teste de trilha")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("run-status")).ToHaveAttributeAsync("data-status", "Succeeded", new LocatorAssertionsToHaveAttributeOptions { Timeout = 180_000 });
        await Expect(page.Locator("[data-testid=run-flow] [data-step][data-status=Succeeded]")).ToHaveCountAsync(4);
        var pipeline = await App.Host.Admin.GetPipelineAsync("calculo-progressao", Ct);
        pipeline!.LatestPublishedVersion.ShouldBe(1);
        pipeline.Versions.Select(v => (v.Version, v.Status)).ShouldBe([(2, VersionStatus.Draft), (1, VersionStatus.Published)], ignoreOrder: true);
    }

    [Fact]
    public async Task Spec008_AC03_Organize_KeepsEveryConnection_IncludingThePipelineOutputs()
    {
        await StartAsync(s => s.Worker = false);
        await App.ImportFixtureAsync("calculo-progressao");
        await App.Host.Admin.CreatePipelineDraftFromAsync("calculo-progressao", 1, PsrEnvironment.Author, Ct);
        var page = await OpenAsync(PanelUsers.Author);
        await GoToAsync(page, "trilhas/calculo-progressao/editar");
        var canvas = page.GetByTestId("pipeline-canvas");
        var edges = canvas.Locator(".vue-flow__edge");
        var outputs = canvas.Locator(".vue-flow__edge[data-id^='out:']");
        await Expect(outputs).ToHaveCountAsync(4);
        var before = await edges.CountAsync();

        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Organizar" }).ClickAsync();

        await Expect(page.GetByTestId("dirty")).ToBeVisibleAsync();
        await Expect(outputs).ToHaveCountAsync(4);
        await Expect(edges).ToHaveCountAsync(before);
    }

    private static ILocator Step(ILocator canvas, string key) => canvas.Locator($"[data-step={key}]");

    private static ILocator In(ILocator node, string name) => node.Locator($"[data-handle='in:{name}']");

    private static ILocator Out(ILocator node, string name) => node.Locator($"[data-handle='out:{name}']");

    private static Regex DisabledHandle() => new(@"\bhandle--disabled\b");

    /// <summary>Adds a pipeline input with the field builder of the side panel.</summary>
    private static async Task AddFieldAsync(ILocator builder, string name, string type, string? itemType = null)
    {
        await builder.GetByTestId("add-field").ClickAsync();
        var panel = builder.Locator(".v-expansion-panel").Last;
        await panel.Locator(".v-expansion-panel-title").ClickAsync();
        await panel.GetByLabel("Nome", new LocatorGetByLabelOptions { Exact = true }).FillAsync(name);
        await panel.ChooseAsync("Tipo", type);
        if (itemType is not null)
        {
            await panel.ChooseAsync("Tipo dos itens", itemType);
        }

        await panel.Locator(".v-expansion-panel-title").ClickAsync();
    }
}
