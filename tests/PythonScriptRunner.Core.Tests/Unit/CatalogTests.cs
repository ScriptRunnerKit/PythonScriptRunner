using PythonScriptRunner.Core.Tests.Infrastructure;

namespace PythonScriptRunner.Core.Tests.Unit;

public sealed class CatalogTests(PythonFixture fixture)
{
    [Fact]
    public async Task Spec001_AC26_InvalidManifest_FailsWithFileAndJsonPath()
    {
        // Arrange
        using var catalog = new TempCatalog();
        var folder = catalog.AddStage("eco", """
            { "key": "eco", "version": 1, "name": "Eco", "inputs": [ { "name": "x", "type": "texto" } ], "outputs": [] }
            """, Scripts.Echo);
        await using var host = RunnerHost.Create(fixture, b => b.AddFileCatalog(catalog.Root));

        // Act
        var exception = await Should.ThrowAsync<PsrConfigurationException>(() =>
            host.Services.GetRequiredService<ICatalog>().FindStageAsync("eco", null, TestContext.Current.CancellationToken).AsTask());

        // Assert
        exception.Message.ShouldContain(Path.Combine(folder, "stage.json"));
        exception.Message.ShouldContain("/inputs/0/type");
    }

    [Fact]
    public async Task Spec001_AC27_UnknownStage_ThrowsStageNotFound()
    {
        await using var host = RunnerHost.Create(fixture);

        await Should.ThrowAsync<StageNotFoundException>(() => host.Runner.RunStageAsync("inexistente", null));
    }

    [Fact]
    public async Task UnknownVersion_ThrowsVersionNotFound()
    {
        await using var host = RunnerHost.Create(fixture, b => b.AddStage("eco", s => s.Script(Scripts.Echo)));

        var exception = await Should.ThrowAsync<VersionNotFoundException>(() =>
            host.Runner.RunStageAsync("eco", null, new RunOptions { Version = 9 }));

        exception.Version.ShouldBe(9);
    }

    [Fact]
    public async Task LatestVersion_IsTheHighest()
    {
        await using var host = RunnerHost.Create(fixture, b => b
            .AddStage("eco", s => s.Version(1).Script(Scripts.Echo))
            .AddStage("eco", s => s.Version(3).Script(Scripts.Echo)));

        var stage = await host.Services.GetRequiredService<ICatalog>().FindStageAsync("eco", null, TestContext.Current.CancellationToken);

        stage!.Version.ShouldBe(3);
    }

    [Fact]
    public async Task DuplicateKeyAndVersion_IsAConfigurationError()
    {
        using var catalog = new TempCatalog();
        catalog.AddStage("eco", Scripts.EchoManifest, Scripts.Echo);
        await using var host = RunnerHost.Create(fixture, b => b
            .AddFileCatalog(catalog.Root)
            .AddStage("eco", s => s.Script(Scripts.Echo)));

        var exception = await Should.ThrowAsync<PsrConfigurationException>(() =>
            host.Services.GetRequiredService<ICatalog>().FindStageAsync("eco", null, TestContext.Current.CancellationToken).AsTask());

        exception.Message.ShouldContain("defined twice");
    }

    [Fact]
    public async Task FolderCatalog_UsesRequirementsLock_WhenPresent()
    {
        using var catalog = new TempCatalog();
        catalog.AddStage("eco", Scripts.EchoManifest, Scripts.Echo, requirements: "six\n", requirementsLock: "six==1.17.0\n");
        await using var host = RunnerHost.Create(fixture, b => b.AddFileCatalog(catalog.Root));

        var stage = await host.Services.GetRequiredService<ICatalog>().FindStageAsync("eco", 1, TestContext.Current.CancellationToken);

        stage!.Requirements.ShouldBe("six\n");
        stage.LockedRequirements.ShouldBe("six==1.17.0\n");
        stage.Script.ShouldBeOfType<FileScriptSource>();
    }

    [Fact]
    public void FluentStage_WithInvalidDefinition_FailsImmediately()
    {
        var services = new ServiceCollection();

        var exception = Should.Throw<PsrConfigurationException>(() => services.AddPythonScriptRunner()
            .AddStage("Inválida", s => s.Script(Scripts.Echo).Input("Nome", FieldType.String)));

        exception.Message.ShouldContain("key must be a slug");
        exception.Message.ShouldContain("inputs.Nome");
    }
}
