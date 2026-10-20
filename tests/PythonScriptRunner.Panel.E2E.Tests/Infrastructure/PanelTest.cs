using System.Text.RegularExpressions;

namespace PythonScriptRunner.Panel.E2E.Tests.Infrastructure;

/// <summary>
/// Base of the panel tests: starts the deployment on demand and opens browser contexts (pt-BR, São Paulo, 1600×1000).
/// Failed tests keep a Playwright trace in <c>TestResults/traces</c> next to the test assembly.
/// </summary>
public abstract class PanelTest(PostgresFixture postgres, BrokerFixture broker, PythonFixture python, BrowserFixture browser) : IAsyncLifetime
{
    private readonly List<IBrowserContext> _contexts = [];
    private PanelApp? _app;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected PanelApp App => _app ?? throw new InvalidOperationException("Call StartAsync first.");

    /// <summary>Starts the deployment of this test.</summary>
    protected async Task<PanelApp> StartAsync(Action<PanelAppSettings>? configure = null) =>
        _app = await PanelApp.StartAsync(postgres, broker, python, configure);

    /// <summary>Opens a page in a new browser context, signed in as <paramref name="user"/> when given (through the login API).</summary>
    protected async Task<IPage> OpenAsync(string? user = null, Action<BrowserNewContextOptions>? configure = null)
    {
        var options = new BrowserNewContextOptions
        {
            Locale = "pt-BR",
            TimezoneId = "America/Sao_Paulo",
            ViewportSize = new ViewportSize { Width = 1600, Height = 1000 },
            ColorScheme = ColorScheme.Light,
        };
        configure?.Invoke(options);
        var context = await browser.Browser.NewContextAsync(options);
        _contexts.Add(context);
        context.SetDefaultTimeout(30_000);
        await context.Tracing.StartAsync(new TracingStartOptions { Screenshots = true, Snapshots = true });
        if (user is not null)
        {
            await SignInAsync(context, user);
        }

        return await context.NewPageAsync();
    }

    /// <summary>Signs a browser context in through <c>POST /auth/login</c> (the cookie is kept by the context).</summary>
    protected async Task SignInAsync(IBrowserContext context, string user)
    {
        var response = await context.APIRequest.PostAsync(App.UrlOf("api/auth/login"), new APIRequestContextOptions
        {
            DataObject = new { name = user, password = PanelUsers.Password },
            Headers = new Dictionary<string, string> { ["X-PSR-Request"] = "1" },
        });
        response.Status.ShouldBe(204, await response.TextAsync());
    }

    /// <summary>Navigates to a panel path (e.g. <c>etapas/eco</c>).</summary>
    protected Task GoToAsync(IPage page, string path) => page.GotoAsync(App.UrlOf(path));

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        var failed = TestContext.Current.TestState?.Result == TestResult.Failed;
        var name = Regex.Replace(TestContext.Current.TestMethod?.MethodName ?? "test", "[^A-Za-z0-9_-]", "_");
        for (var i = 0; i < _contexts.Count; i++)
        {
            try
            {
                await _contexts[i].Tracing.StopAsync(failed
                    ? new TracingStopOptions { Path = Path.Combine(AppContext.BaseDirectory, "TestResults", "traces", $"{name}-{i + 1}.zip") }
                    : new TracingStopOptions());
                await _contexts[i].CloseAsync();
            }
            catch (PlaywrightException)
            {
                // The context may already be gone with the browser.
            }
        }

        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
