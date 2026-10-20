[assembly: AssemblyFixture(typeof(PythonScriptRunner.Panel.E2E.Tests.Infrastructure.BrowserFixture))]

namespace PythonScriptRunner.Panel.E2E.Tests.Infrastructure;

/// <summary>
/// One Chromium for the whole run (each test opens its own contexts). Installs the browser when missing; CI installs it
/// beforehand with <c>playwright.ps1 install --with-deps chromium</c>. <c>PSR_E2E_HEADED=1</c> shows the browser.
/// </summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var code = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (code != 0)
        {
            throw new InvalidOperationException($"Installing Chromium for Playwright failed (exit code {code}).");
        }

        // Live updates and Python runs take longer than the 5 s default of the web assertions.
        SetDefaultExpectTimeout(20_000);
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Environment.GetEnvironmentVariable("PSR_E2E_HEADED") != "1",
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }
}
