using Deque.AxeCore.Playwright;
using PythonScriptRunner.Panel.E2E.Tests.Infrastructure;

namespace PythonScriptRunner.Panel.E2E.Tests;

/// <summary>Spec 008, AC-15: axe-core finds no serious or critical violation on the login, a list, a form and a run.</summary>
public sealed class AccessibilityTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python, BrowserFixture browser)
    : PanelTest(postgres, broker, python, browser)
{
    [Fact]
    public async Task Spec008_AC15_LoginListFormAndRunDetail_HaveNoSeriousOrCriticalViolations()
    {
        await StartAsync();
        await App.ImportFixtureAsync("calculo-progressao");
        var run = await App.Host.Queue.EnqueuePipelineAsync("calculo-progressao", PanelScripts.Calculo, cancellationToken: Ct);
        await App.Host.WaitForRunAsync(run.RunId, timeout: TimeSpan.FromMinutes(3));
        var page = await OpenAsync();

        await GoToAsync(page, "entrar");
        await Expect(page.GetByLabel("Senha")).ToBeVisibleAsync();
        await ExpectAccessibleAsync(page, "login");
        await page.Locator("form").FillLoginAsync(PanelUsers.Executor, PanelUsers.Password);
        await Expect(page).ToHaveURLAsync(App.Url.ToString());

        await GoToAsync(page, "execucoes");
        await Expect(page.GetByTestId("runs-table").Locator("tbody tr").First).ToBeVisibleAsync();
        await ExpectAccessibleAsync(page, "lista de execuções");

        await GoToAsync(page, "trilhas/calculo-progressao");
        await page.GetByTestId("run-pipeline").ClickAsync();
        await Expect(page.GetByTestId("run-dialog").Locator("[data-field=a1]").First).ToBeVisibleAsync();
        await ExpectAccessibleAsync(page, "formulário de disparo");

        await GoToAsync(page, $"execucoes/{run.RunId}");
        await Expect(page.Locator("[data-testid=run-flow] [data-step]")).ToHaveCountAsync(4);
        await ExpectAccessibleAsync(page, "detalhe da execução");
    }

    private static async Task ExpectAccessibleAsync(IPage page, string screen)
    {
        var result = await page.RunAxe();
        var violations = result.Violations
            .Where(v => v.Impact is "serious" or "critical")
            .Select(v => $"{v.Id} ({v.Impact}): {v.Help} — {string.Join(" | ", v.Nodes.Select(n => $"{n.Target} {n.Html} {string.Join(" ", n.Any.Concat(n.All).Concat(n.None).Select(c => c.Message))}"))}")
            .ToList();
        violations.ShouldBeEmpty($"{screen}:\n{string.Join("\n", violations)}");
    }
}
