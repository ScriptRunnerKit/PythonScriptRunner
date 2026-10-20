namespace PythonScriptRunner.Panel.E2E.Tests.Infrastructure;

/// <summary>Interactions with the panel widgets that need more than a click (Monaco, Vuetify selects, Vue Flow handles).</summary>
public static class PanelPage
{
    /// <summary>Signs in on <c>/entrar</c> (or in the expired session dialog) with the form.</summary>
    public static async Task FillLoginAsync(this ILocator form, string? name, string password)
    {
        if (name is not null)
        {
            await form.GetByLabel("Usuário").FillAsync(name);
        }

        await form.GetByLabel("Senha").FillAsync(password);
        await form.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Entrar", Exact = true }).ClickAsync();
    }

    /// <summary>
    /// Replaces the text of a Monaco editor by pasting it over a select-all (typing would let Monaco indent each new line
    /// on its own and break the Python indentation).
    /// </summary>
    public static async Task SetCodeAsync(this ILocator editor, string code)
    {
        var monaco = editor.Locator(".monaco-editor");
        await Expect(monaco).ToBeVisibleAsync();
        await monaco.Locator(".view-lines").ClickAsync();
        await editor.Page.Keyboard.PressAsync("ControlOrMeta+A");
        await editor.Page.EvaluateAsync(
            """
            text => {
                const data = new DataTransfer();
                data.setData('text/plain', text);
                document.activeElement.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
            }
            """,
            code.ReplaceLineEndings("\n"));
    }

    /// <summary>Chooses an option of a Vuetify select (the field is located by its label inside <paramref name="scope"/>).</summary>
    public static async Task ChooseAsync(this ILocator scope, string label, string option)
    {
        var page = scope.Page;
        await scope.Locator(".v-select, .v-autocomplete").Filter(new LocatorFilterOptions { Has = page.GetByText(label, new PageGetByTextOptions { Exact = true }) })
            .Locator(".v-field").First.ClickAsync();
        var item = page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = option, Exact = true });
        var menu = page.Locator(".v-overlay--active .v-overlay__content").Last;
        await Expect(menu).ToBeVisibleAsync();

        // The menu renders only the items in view (virtual scroll): scroll until the option exists, then click it where it is
        // (scrolling it into view again would make the list render it anew).
        for (var i = 0; i < 20 && await item.CountAsync() == 0; i++)
        {
            await menu.EvaluateAsync("menu => [menu, ...menu.querySelectorAll('*')].filter(e => e.scrollHeight > e.clientHeight + 1).forEach(e => e.scrollTop += 120)");
        }

        await item.DispatchEventAsync("click");
        await Expect(menu).ToBeHiddenAsync();
    }

    /// <summary>Fills the generated form of <c>calculo-progressao</c> with <see cref="PanelScripts.Calculo"/>.</summary>
    public static async Task FillCalculoAsync(this ILocator form)
    {
        await form.Field("a1").FillAsync("1");
        await form.Field("razao").FillAsync("2");
        await form.Field("n").FillAsync("3");
        var add = form.Locator("[data-field=coeficientes]").First.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Adicionar item" });
        await add.ClickAsync();
        await add.ClickAsync();
        await form.Field("coeficientes[0]").FillAsync("0");
        await form.Field("coeficientes[1]").FillAsync("1");
    }

    /// <summary>The text box of a field of a generated form.</summary>
    public static ILocator Field(this ILocator form, string name) =>
        form.Locator($"[data-field='{name}']").First.GetByRole(AriaRole.Textbox).First;

    /// <summary>Presses the mouse on a Vue Flow handle and starts dragging a connection from it.</summary>
    public static async Task BeginConnectionAsync(this IPage page, ILocator source)
    {
        var (x, y) = await CenterAsync(source);
        await page.Mouse.MoveAsync(x, y);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(x + 30, y + 15, new MouseMoveOptions { Steps = 5 });
    }

    /// <summary>Moves the connection being dragged over <paramref name="target"/> and releases it there.</summary>
    public static async Task EndConnectionAsync(this IPage page, ILocator target)
    {
        var (x, y) = await CenterAsync(target);
        await page.Mouse.MoveAsync(x, y, new MouseMoveOptions { Steps = 10 });
        await page.Mouse.UpAsync();
    }

    /// <summary>Connects two Vue Flow handles by dragging (the target may only appear while dragging, like "new output").</summary>
    public static async Task ConnectAsync(this IPage page, ILocator source, ILocator target)
    {
        await page.BeginConnectionAsync(source);
        await page.EndConnectionAsync(target);
    }

    private static async Task<(float X, float Y)> CenterAsync(ILocator locator)
    {
        await Expect(locator).ToBeVisibleAsync();
        var box = await locator.BoundingBoxAsync() ?? throw new InvalidOperationException($"{locator} has no box.");
        return (box.X + (box.Width / 2), box.Y + (box.Height / 2));
    }
}
