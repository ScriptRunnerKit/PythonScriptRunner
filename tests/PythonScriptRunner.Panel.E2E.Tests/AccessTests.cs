using System.Globalization;
using System.Text.RegularExpressions;
using PythonScriptRunner.Panel.E2E.Tests.Infrastructure;

namespace PythonScriptRunner.Panel.E2E.Tests;

/// <summary>Spec 008: fixed login, expired session, roles and theme in the real panel.</summary>
public sealed partial class AccessTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python, BrowserFixture browser)
    : PanelTest(postgres, broker, python, browser)
{
    [Fact]
    public async Task Spec008_AC17_FixedLogin_GoesToEntrar_RejectsAWrongPassword_ReturnsToTheRoute_AndLogsOut()
    {
        await StartAsync();
        var page = await OpenAsync();

        await GoToAsync(page, "execucoes");
        await Expect(page).ToHaveURLAsync(App.UrlOf("entrar?volta=/execucoes"));
        var form = page.Locator("form");
        await form.FillLoginAsync(PanelUsers.Executor, "senha-errada");
        await Expect(page.GetByText("Usuário ou senha inválidos.")).ToBeVisibleAsync();
        await form.FillLoginAsync(null, PanelUsers.Password);

        await Expect(page).ToHaveURLAsync(App.UrlOf("execucoes"));
        var menu = page.Locator(".v-navigation-drawer");
        await Expect(MenuItem(menu, "Execuções")).ToBeVisibleAsync();
        await Expect(MenuItem(menu, "Agendamentos")).ToBeVisibleAsync();
        await Expect(MenuItem(menu, "Auditoria")).ToHaveCountAsync(0);
        await Expect(MenuItem(menu, "Administração")).ToHaveCountAsync(0);

        await page.GetByTestId("user-menu").ClickAsync();
        await page.GetByTestId("logout").ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/psr/entrar"));
        (await page.APIRequest.GetAsync(App.UrlOf("api/me"))).Status.ShouldBe(401);
    }

    [Fact]
    public async Task Spec008_AC18_TheEleventhAttemptInAMinute_ShowsTheCountdown_AndTheLoginWorksWhenItEnds()
    {
        await StartAsync();
        var page = await OpenAsync();
        await GoToAsync(page, "entrar");
        var form = page.Locator("form");
        var submit = form.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Entrar", Exact = true });

        for (var i = 1; i <= 10; i++)
        {
            // Unknown users: the lockout of a user (5 failures) is not what this test is about.
            await form.FillLoginAsync($"ninguem{i}", "senha-errada");
            await Expect(form.GetByLabel("Senha")).ToHaveValueAsync(string.Empty);
        }

        await form.FillLoginAsync(PanelUsers.Author, PanelUsers.Password);
        var countdown = page.GetByTestId("login-throttled");
        await Expect(countdown).ToContainTextAsync(CountdownText());
        await Expect(submit).ToBeDisabledAsync();
        var seconds = int.Parse(CountdownText().Match(await countdown.InnerTextAsync()).Groups[1].Value, CultureInfo.InvariantCulture);
        seconds.ShouldBeInRange(1, 60);
        await Expect(countdown).Not.ToContainTextAsync($"em {seconds} s");

        await Expect(countdown).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 75_000 });
        await Expect(submit).ToBeEnabledAsync();
        await submit.ClickAsync();
        await Expect(page).ToHaveURLAsync(App.Url.ToString());
    }

    [Fact]
    public async Task Spec008_AC19_ExpiredSession_AsksTheSameUserInADialog_AndFinishesTheSave()
    {
        await StartAsync();
        await App.SaveStageDraftAsync("eco", PanelScripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);
        var page = await OpenAsync(PanelUsers.Author);
        await GoToAsync(page, "etapas/eco/editar");
        await page.Locator("[data-testid=field-builder][data-kind=inputs]").GetByTestId("add-field").ClickAsync();
        await Expect(page.GetByTestId("dirty")).ToBeVisibleAsync();

        // The session ends (the cookie is gone, as when it expires): the save answers 401.
        await page.Context.ClearCookiesAsync();
        await page.GetByTestId("save").ClickAsync();

        var dialog = page.Locator(".v-dialog").Filter(new LocatorFilterOptions { HasText = "Sua sessão expirou" });
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.GetByLabel("Usuário")).ToHaveValueAsync(PanelUsers.Author);
        await Expect(dialog.GetByLabel("Usuário")).Not.ToBeEditableAsync();
        await dialog.FillLoginAsync(null, PanelUsers.Password);

        await Expect(dialog).ToBeHiddenAsync();
        await Expect(page.GetByTestId("dirty")).ToBeHiddenAsync();
        var saved = await App.Host.Admin.GetStageDraftAsync("eco", Ct);
        saved!.Data.Inputs.Select(f => f.Name).ShouldBe(["texto", "campo_2"]);
    }

    [Fact]
    public async Task Spec008_AC10_Reader_SeesNoActionButtons_AndEditRoutesShowNoPermission()
    {
        await StartAsync(s => s.Worker = false);
        await App.ImportFixtureAsync("calculo-progressao");
        var run = await App.Host.Queue.EnqueuePipelineAsync("calculo-progressao", PanelScripts.Calculo, cancellationToken: Ct);
        var executor = await OpenAsync(PanelUsers.Executor);
        var reader = await OpenAsync(PanelUsers.Reader);

        // What the reader must not see is there for the executor (no worker: the run stays queued and cancellable).
        await GoToAsync(executor, $"execucoes/{run.RunId}");
        await Expect(executor.GetByTestId("cancel-run")).ToBeVisibleAsync();
        await GoToAsync(executor, "trilhas/calculo-progressao");
        await Expect(executor.GetByTestId("run-pipeline")).ToBeVisibleAsync();

        await GoToAsync(reader, $"execucoes/{run.RunId}");
        await Expect(reader.GetByTestId("run-status")).ToBeVisibleAsync();
        await Expect(reader.GetByTestId("cancel-run")).ToHaveCountAsync(0);
        await GoToAsync(reader, "trilhas/calculo-progressao");
        await Expect(reader.GetByTestId("versions-table")).ToBeVisibleAsync();
        await ExpectNoneAsync(reader, "run-pipeline", "edit-pipeline", "delete-pipeline");
        await GoToAsync(reader, "etapas");
        await Expect(reader.Locator("[data-key=progressao-termos]")).ToBeVisibleAsync();
        await ExpectNoneAsync(reader, "create", "import-zip");
        await GoToAsync(reader, "etapas/progressao-termos");
        await Expect(reader.GetByTestId("versions-table")).ToBeVisibleAsync();
        await ExpectNoneAsync(reader, "edit-stage", "delete-stage");
        await GoToAsync(reader, "agendamentos");
        await Expect(reader.GetByTestId("schedules-table")).ToBeVisibleAsync();
        await ExpectNoneAsync(reader, "create-schedule");

        foreach (var path in new[] { "etapas/progressao-termos/editar", "trilhas/calculo-progressao/editar", "auditoria", "administracao" })
        {
            await GoToAsync(reader, path);
            await Expect(reader.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Sem permissão" })).ToBeVisibleAsync();
        }
    }

    [Fact]
    public async Task Spec008_AC12_FollowsTheDarkSystemTheme_AndKeepsTheManualChoiceAfterReload()
    {
        await StartAsync();
        var page = await OpenAsync(PanelUsers.Reader, o => o.ColorScheme = ColorScheme.Dark);
        await GoToAsync(page, string.Empty);
        var app = page.Locator(".v-application");
        await Expect(app).ToHaveClassAsync(new Regex(@"\bv-theme--dark\b"));

        await page.GetByTestId("theme-menu").ClickAsync();
        await page.GetByTestId("theme-light").ClickAsync();
        await Expect(app).ToHaveClassAsync(new Regex(@"\bv-theme--light\b"));
        await page.ReloadAsync();

        await Expect(app).ToHaveClassAsync(new Regex(@"\bv-theme--light\b"));
    }

    private static ILocator MenuItem(ILocator menu, string text) =>
        menu.GetByText(text, new LocatorGetByTextOptions { Exact = true });

    private static async Task ExpectNoneAsync(IPage page, params string[] testIds)
    {
        foreach (var id in testIds)
        {
            await Expect(page.GetByTestId(id)).ToHaveCountAsync(0);
        }
    }

    [GeneratedRegex(@"Tente de novo em (\d+) s")]
    private static partial Regex CountdownText();
}
