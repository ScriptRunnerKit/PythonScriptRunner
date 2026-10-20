using PythonScriptRunner.EntityFrameworkCore.Tests.Infrastructure;
using static PythonScriptRunner.EntityFrameworkCore.Tests.Infrastructure.TestScripts;

namespace PythonScriptRunner.EntityFrameworkCore.Tests;

public sealed class CatalogAdminTests(PostgresFixture postgres, PythonFixture python)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spec004_AC03_PublishLocksDependenciesAndAudits()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python);

        var published = await host.PublishStageAsync("eco", Echo, [Text("texto")], [Text("texto")], requirements: "six==1.16.0");

        published.Status.ShouldBe(VersionStatus.Published);
        published.LockedRequirements.ShouldNotBeNull().ShouldContain("six==1.16.0");
        published.LockedRequirements.ShouldContain("--hash=sha256:");
        published.EnvironmentHash.ShouldNotBeNull().Length.ShouldBe(64);
        published.PublishedBy.ShouldBe(PsrTestHost.Author.Subject);
        (await host.Admin.GetStageAsync("eco", Ct))!.LatestPublishedVersion.ShouldBe(1);
        (await host.ScalarAsync<string>("SELECT actor FROM psr.audit_events WHERE action = 'StagePublished' AND entity_key = 'eco@1'"))
            .ShouldBe(PsrTestHost.Author.Subject);
    }

    [Fact]
    public async Task Spec004_AC04_SyntaxErrorReportsLineAndColumnAndKeepsTheDraft()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python);
        var draft = await host.Admin.CreateStageAsync(new NewCatalogItem("quebrado", "Quebrado"), PsrTestHost.Author, Ct);
        var saved = await host.Admin.SaveStageDraftAsync("quebrado", 1, draft.Data with { Script = "from psrunner import stage\n\ndef run(inputs:\n" },
            draft.RowVersion, PsrTestHost.Author, Ct);

        var error = await Should.ThrowAsync<ScriptSyntaxException>(() => host.Admin.PublishStageAsync("quebrado", 1, saved.RowVersion, PsrTestHost.Author, Ct));

        error.Code.ShouldBe(PsrErrorCodes.ScriptSyntaxError);
        error.Line.ShouldBe(3);
        error.Column.ShouldNotBeNull();
        (await host.Admin.GetStageDraftAsync("quebrado", Ct)).ShouldNotBeNull().Version.ShouldBe(1);
    }

    [Fact]
    public async Task Spec004_AC05_UnknownDependencyFailsWithTheUvMessage()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python);

        var error = await Should.ThrowAsync<PsrDomainException>(() =>
            host.PublishStageAsync("dependencia", Echo, [Text("texto")], [Text("texto")], requirements: "pacote-inexistente-psr-0000==1.0"));

        error.Code.ShouldBe(PsrErrorCodes.DependencyResolutionFailed);
        error.Message.ShouldContain("pacote-inexistente-psr-0000");
        (await host.Admin.GetStageDraftAsync("dependencia", Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Spec004_AC06_PublishedVersionsAreImmutableAndDraftsCopyThem()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python);
        var v1 = await host.PublishStageAsync("eco", Echo, [Text("texto")], [Text("texto")]);

        var error = await Should.ThrowAsync<PsrDomainException>(() =>
            host.Admin.SaveStageDraftAsync("eco", 1, v1.Data with { Script = "mudou" }, v1.RowVersion, PsrTestHost.Author, Ct));
        var draft = await host.Admin.CreateStageDraftFromAsync("eco", 1, PsrTestHost.Author, Ct);

        error.Code.ShouldBe(PsrErrorCodes.VersionImmutable);
        draft.Version.ShouldBe(2);
        draft.Data.Script.ShouldBe(Echo);
        draft.Data.Inputs.Single().Name.ShouldBe("texto");
        (await Should.ThrowAsync<PsrDomainException>(() => host.Admin.CreateStageDraftFromAsync("eco", 1, PsrTestHost.Author, Ct)))
            .Code.ShouldBe(PsrErrorCodes.DraftAlreadyExists);
    }

    [Fact]
    public async Task Spec004_AC07_SecondSaveWithTheSameRowVersionConflicts()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python);
        var draft = await host.Admin.CreateStageAsync(new NewCatalogItem("disputado", "Disputado"), PsrTestHost.Author, Ct);

        var first = await host.Admin.SaveStageDraftAsync("disputado", 1, draft.Data with { Script = "# autora 1" }, draft.RowVersion, PsrTestHost.Author, Ct);
        var error = await Should.ThrowAsync<ConcurrencyConflictException>(() =>
            host.Admin.SaveStageDraftAsync("disputado", 1, draft.Data with { Script = "# autora 2" }, draft.RowVersion, Actor.User("outra@teste"), Ct));

        error.Code.ShouldBe(PsrErrorCodes.ConcurrencyConflict);
        var current = error.Current.ShouldBeOfType<StageDraft>();
        current.Data.Script.ShouldBe("# autora 1");
        current.RowVersion.ShouldBe(first.RowVersion);
    }

    [Fact]
    public async Task Spec004_AC08_ArchivingAStageUsedByAPublishedPipelineIsBlocked()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python);
        await host.PublishStageAsync("eco", Echo, [Text("texto")], [Text("texto")]);
        await host.PublishPipelineAsync("usa-eco", EchoPipeline());

        var error = await Should.ThrowAsync<VersionReferencedException>(() => host.Admin.ArchiveStageVersionAsync("eco", 1, PsrTestHost.Author, Ct));

        error.Code.ShouldBe(PsrErrorCodes.VersionReferenced);
        error.Pipelines.ShouldBe(["usa-eco@1"]);
    }

    [Fact]
    public async Task Spec004_AC09_OnlyUnusedStagesCanBeDeleted()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python);
        await host.PublishStageAsync("nunca-usado", Echo, [Text("texto")], [Text("texto")]);
        await host.PublishStageAsync("eco", Echo, [Text("texto")], [Text("texto")]);
        await host.PublishPipelineAsync("usa-eco", EchoPipeline());
        await host.PublishStageAsync("executado", Echo, [Text("texto")], [Text("texto")]);
        await using (var result = await host.Runner.RunStageAsync("executado", new { texto = "x" }, cancellationToken: Ct))
        {
            result.EnsureSuccess();
        }

        await host.Admin.DeleteStageAsync("nunca-usado", Actor.User("admin@teste"), Ct);

        (await host.Admin.GetStageAsync("nunca-usado", Ct)).ShouldBeNull();
        (await host.ScalarAsync<long>("SELECT count(*) FROM psr.stage_versions v JOIN psr.stages s ON s.id = v.stage_id WHERE s.key = 'nunca-usado'")).ShouldBe(0);
        (await Should.ThrowAsync<PsrDomainException>(() => host.Admin.DeleteStageAsync("eco", PsrTestHost.Author, Ct))).Code.ShouldBe(PsrErrorCodes.DeleteNotAllowed);
        (await Should.ThrowAsync<PsrDomainException>(() => host.Admin.DeleteStageAsync("executado", PsrTestHost.Author, Ct))).Code.ShouldBe(PsrErrorCodes.DeleteNotAllowed);
    }

    [Fact]
    public async Task Spec004_AC16_PipelineRetentionCannotExceedTheGlobalOne()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python);
        var draft = await host.Admin.CreatePipelineAsync(new NewCatalogItem("retida", "Retida"), PsrTestHost.Author, Ct);
        var pipeline = (await host.Admin.GetPipelineAsync("retida", Ct))!;

        var error = await Should.ThrowAsync<PsrDomainException>(() =>
            host.Admin.UpdatePipelineRetentionAsync("retida", new PipelineRetention(30, null), pipeline.RowVersion, PsrTestHost.Author, Ct));
        var shorter = await host.Admin.UpdatePipelineRetentionAsync("retida", new PipelineRetention(2, 30), pipeline.RowVersion, PsrTestHost.Author, Ct);

        error.Code.ShouldBe(PsrErrorCodes.RetentionExceedsGlobal);
        shorter.Retention.ShouldBe(new PipelineRetention(2, 30));
        draft.Version.ShouldBe(1);
    }

    [Fact]
    public async Task ImportCreatesDraftsAndExistingKeysGetANewVersion()
    {
        await using var source = await PsrTestHost.CreateAsync(postgres, python);
        await source.PublishStageAsync("eco", Echo, [Text("texto")], [Text("texto")]);
        await source.PublishPipelineAsync("usa-eco", EchoPipeline());
        using var zip = new MemoryStream();
        await source.Admin.ExportPipelineVersionAsync("usa-eco", 1, zip, Ct);

        await using var target = await PsrTestHost.CreateAsync(postgres, python);
        zip.Position = 0;
        var fresh = await target.Admin.ImportAsync(zip, PsrTestHost.Author, Ct);
        zip.Position = 0;
        var again = await source.Admin.ImportAsync(zip, PsrTestHost.Author, Ct);

        fresh.Items.ShouldBe([new ImportedItem("Stage", "eco", 1, true), new ImportedItem("Pipeline", "usa-eco", 1, true)]);
        again.Items.ShouldBe([new ImportedItem("Stage", "eco", 2, false), new ImportedItem("Pipeline", "usa-eco", 2, false)]);
        var pipelineDraft = (await source.Admin.GetPipelineDraftAsync("usa-eco", Ct))!;
        pipelineDraft.Data.Steps.Single().StageVersion.ShouldBe(2, "references follow the stage imported in the same archive");
        (await source.Admin.GetStageVersionAsync("eco", 1, Ct))!.Status.ShouldBe(VersionStatus.Published);
        zip.Position = 0;
        (await Should.ThrowAsync<DefinitionInvalidException>(() => source.Admin.ImportAsync(zip, PsrTestHost.Author, Ct)))
            .Code.ShouldBe(PsrErrorCodes.ImportInvalid);
    }

    private static PipelineDraftData EchoPipeline() => new()
    {
        Inputs = [Text("texto")],
        Steps = [new PipelineStepDefinition("eco", "eco", 1)],
        Outputs = [new PipelineOutputDefinition("texto", From.Step("eco", "texto"))],
    };
}
