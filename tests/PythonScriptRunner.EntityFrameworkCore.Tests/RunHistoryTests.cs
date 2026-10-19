using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using PythonScriptRunner.EntityFrameworkCore.Entities;
using PythonScriptRunner.EntityFrameworkCore.Tests.Infrastructure;
using static PythonScriptRunner.EntityFrameworkCore.Tests.Infrastructure.TestScripts;

namespace PythonScriptRunner.EntityFrameworkCore.Tests;

public sealed class RunHistoryTests(PostgresFixture postgres, PythonFixture python)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spec004_AC10_SyncRunIsPersistedWithStepsAttemptsLogsAndFiles()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python);
        await host.PublishStageAsync("gera-arquivo", WriteFile, [Text("texto")], [File("arquivo"), new FieldDefinition("tamanho", FieldType.Integer)]);

        await using var result = await host.Runner.RunStageAsync("gera-arquivo", new { texto = "ab" }, cancellationToken: Ct);
        result.EnsureSuccess();
        var run = await host.Runs.GetAsync(result.Id, Ct);

        run.ShouldNotBeNull();
        run.Status.ShouldBe(RunStatus.Succeeded);
        run.Kind.ShouldBe(RunKind.Stage);
        run.Mode.ShouldBe(ExecutionMode.Sync);
        run.Trigger.ShouldBe(RunTrigger.Library);
        run.TriggeredBy.ShouldBe("system:library");
        run.TargetKey.ShouldBe("gera-arquivo");
        run.Inputs["texto"]!.GetValue<string>().ShouldBe("ab");
        run.FinishedAt.ShouldNotBeNull();
        var step = run.Steps.ShouldHaveSingleItem();
        step.Status.ShouldBe(StepStatus.Succeeded);
        step.Outputs!["tamanho"]!.GetValue<int>().ShouldBe(6);
        var attempt = step.Attempts.ShouldHaveSingleItem();
        attempt.Status.ShouldBe(AttemptStatus.Succeeded);
        attempt.Cause.ShouldBe(AttemptCause.Initial);
        attempt.EnvironmentHash.ShouldNotBeNull();
        attempt.Logs.ShouldContain(LogStream.Stdout);
        (await host.Runs.GetAttemptLogAsync(run.Id, attempt.Id, LogStream.Stdout, Ct))!.Content.ShouldContain("arquivo gerado");

        var artifact = run.Artifacts.ShouldHaveSingleItem();
        artifact.Direction.ShouldBe(ArtifactDirection.Output);
        artifact.StepKey.ShouldBe("gera-arquivo");
        artifact.FileName.ShouldBe("saida.txt");
        artifact.SizeBytes.ShouldBe(6);
        artifact.Sha256.ShouldBe(result.GetFile("arquivo").Sha256);
        run.Outputs!["arquivo"]!["$artifact"]!.GetValue<string>().ShouldBe(artifact.Id.ToString());
        await using var content = await host.Artifacts.OpenReadAsync(artifact.Id, Ct);
        (await new StreamReader(content).ReadToEndAsync(Ct)).ShouldBe("ababab");
    }

    [Fact]
    public async Task FilesPassedBetweenStepsAreStoredOnce()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python);
        await host.PublishStageAsync("gera-arquivo", WriteFile, [Text("texto")], [File("arquivo"), new FieldDefinition("tamanho", FieldType.Integer)]);
        await host.PublishStageAsync("le-arquivo", ReadFile, [File("arquivo")], [Text("conteudo")]);
        await host.PublishPipelineAsync("arquivo-entre-passos", new PipelineDraftData
        {
            Inputs = [Text("texto"), File("anexo")],
            Steps = [new PipelineStepDefinition("gera", "gera-arquivo", 1), new PipelineStepDefinition("le", "le-arquivo", 1)],
            Outputs = [new PipelineOutputDefinition("conteudo", From.Step("le", "conteudo")), new PipelineOutputDefinition("arquivo", From.Step("gera", "arquivo"))],
        });

        await using var result = await host.Runner.RunPipelineAsync("arquivo-entre-passos",
            new { texto = "xy", anexo = PsrFile.FromText("entrada", "anexo.txt") }, cancellationToken: Ct);
        result.EnsureSuccess();
        var run = (await host.Runs.GetAsync(result.Id, Ct))!;

        result.GetOutput<string>("conteudo").ShouldBe("xyxyxy");
        run.Artifacts.Count.ShouldBe(2);
        var input = run.Artifacts.Single(a => a.Direction == ArtifactDirection.Input);
        input.FieldName.ShouldBe("anexo");
        run.Inputs["anexo"]!["$artifact"]!.GetValue<string>().ShouldBe(input.Id.ToString());
        var produced = run.Artifacts.Single(a => a.Direction == ArtifactDirection.Output);
        run.Steps[1].Inputs!["arquivo"]!["$artifact"]!.GetValue<string>().ShouldBe(produced.Id.ToString());
        run.Outputs!["arquivo"]!["$artifact"]!.GetValue<string>().ShouldBe(produced.Id.ToString());
    }

    [Fact]
    public async Task Spec004_AC11_FiftyMegabyteFileIsStreamed()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python);
        var runId = await InsertRunAsync(host, DateTimeOffset.UtcNow, RunStatus.Succeeded);
        var path = Path.Combine(Path.GetTempPath(), $"psr-50mb-{Guid.NewGuid():N}.bin");
        await using (var file = new FileStream(path, FileMode.Create))
        {
            var chunk = new byte[1024 * 1024];
            Random.Shared.NextBytes(chunk);
            for (var i = 0; i < 50; i++)
            {
                await file.WriteAsync(chunk, Ct);
            }
        }

        try
        {
            await host.Artifacts.SaveAsync(new NewArtifact { RunId = runId, Direction = ArtifactDirection.Input, FieldName = "aquecimento", FileName = "a.bin", ContentType = "application/octet-stream" },
                new MemoryStream([1, 2, 3]), Ct);
            var before = GC.GetTotalAllocatedBytes(precise: true);
            ArtifactInfo info;
            await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                info = await host.Artifacts.SaveAsync(new NewArtifact
                {
                    RunId = runId,
                    Direction = ArtifactDirection.Input,
                    FieldName = "grande",
                    FileName = "grande.bin",
                    ContentType = "application/octet-stream",
                }, source, Ct);
            }

            var afterWrite = GC.GetTotalAllocatedBytes(precise: true);
            await using (var stored = await host.Artifacts.OpenReadAsync(info.Id, Ct))
            {
                await stored.CopyToAsync(Stream.Null, Ct);
            }

            var afterRead = GC.GetTotalAllocatedBytes(precise: true);

            info.SizeBytes.ShouldBe(50L * 1024 * 1024);
            (afterWrite - before).ShouldBeLessThan(20L * 1024 * 1024, "writing must stream");
            (afterRead - afterWrite).ShouldBeLessThan(20L * 1024 * 1024, "reading must stream");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task Spec004_AC12_SameIdempotencyKeyReturnsTheSameRunFor24Hours()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = await PsrTestHost.CreateAsync(postgres, python, time);
        await host.PublishStageAsync("eco", Echo, [Text("texto")], [Text("texto")]);
        var options = new RunOptions { IdempotencyKey = "pedido-42" };

        await using var first = await host.Runner.RunStageAsync("eco", new { texto = "um" }, options, Ct);
        await using var second = await host.Runner.RunStageAsync("eco", new { texto = "dois" }, options, Ct);
        time.Advance(TimeSpan.FromHours(25));
        await host.Maintenance.RunOnceAsync(Ct);
        await using var third = await host.Runner.RunStageAsync("eco", new { texto = "tres" }, options, Ct);

        first.Deduplicated.ShouldBeFalse();
        second.Id.ShouldBe(first.Id);
        second.Deduplicated.ShouldBeTrue();
        second.Status.ShouldBe(RunStatus.Succeeded);
        second.GetOutput<string>("texto").ShouldBe("um");
        third.Id.ShouldNotBe(first.Id);
        third.Deduplicated.ShouldBeFalse();
        third.GetOutput<string>("texto").ShouldBe("tres");
        (await host.ScalarAsync<long>("SELECT count(*) FROM psr.runs")).ShouldBe(2);
    }

    [Fact]
    public async Task Spec004_AC13_KeysetPagesReturnEveryRunOnceInDescendingOrder()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python);
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var expected = new List<RunId>();
        await using (var db = await host.DbAsync())
        {
            for (var i = 0; i < 1000; i++)
            {
                var id = RunId.New(start.AddMilliseconds(i * 7));
                expected.Add(id);
                db.Runs.Add(Run(id, RunStatus.Succeeded, stageKey: "lista"));
            }

            await db.SaveChangesAsync(Ct);
        }

        var seen = new List<RunSummary>();
        string? cursor = null;
        var page = 0;
        do
        {
            var result = await host.Runs.ListAsync(new RunQuery { StageKey = "lista", From = start.AddMinutes(-1), To = start.AddHours(2), Cursor = cursor, Limit = 50 }, Ct);
            seen.AddRange(result.Items);
            cursor = result.NextCursor;
            if (++page == 3)
            {
                await using var db = await host.DbAsync();
                for (var i = 0; i < 20; i++)
                {
                    db.Runs.Add(Run(RunId.New(start.AddSeconds(30).AddMilliseconds(i)), RunStatus.Succeeded, stageKey: "lista"));
                }

                await db.SaveChangesAsync(Ct);
            }
        }
        while (cursor is not null);

        seen.Select(r => r.Id).ShouldBe(expected.AsEnumerable().Reverse(), "every run once, newest first; runs added meanwhile are newer than the cursor");
        page.ShouldBe(20);
    }

    [Fact]
    public async Task Spec004_AC17_RolledBackCreationLeavesNoOutboxNorIdempotencyKey()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python);
        var idempotency = host.Services.GetRequiredService<IIdempotencyStore>();
        var outbox = host.Services.GetRequiredService<IOutboxWriter>();
        var runId = RunId.New();

        await using (var db = await host.DbAsync())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            (await idempotency.ReserveAsync(db, IdempotencyScopes.Stage("eco"), "chave", runId, Ct)).ShouldBeNull();
            db.Runs.Add(Run(runId, RunStatus.Queued, stageKey: "eco"));
            outbox.Enqueue(db, new OutboxMessage("psr.execute-step.v1", "psr.commands", "psr.step.execute", new JsonObject { ["runId"] = runId.ToString() }));
            await db.SaveChangesAsync(Ct);
            (await host.ScalarAsync<long>("SELECT count(*) FROM psr.outbox_messages")).ShouldBe(0, "not visible before commit");
            await transaction.RollbackAsync(Ct);
        }

        (await host.ScalarAsync<long>("SELECT count(*) FROM psr.outbox_messages")).ShouldBe(0);
        (await host.ScalarAsync<long>("SELECT count(*) FROM psr.idempotency_keys")).ShouldBe(0);
        (await host.ScalarAsync<long>("SELECT count(*) FROM psr.runs")).ShouldBe(0);
    }

    [Fact]
    public async Task Spec004_AC18_OnlyHumanActionsAreAudited()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python);
        var human = PsrTestHost.Author;
        var created = await host.Admin.CreateStageAsync(new NewCatalogItem("auditado", "Antes"), human, Ct);
        var stage = (await host.Admin.GetStageAsync("auditado", Ct))!;
        await host.Admin.UpdateStageAsync("auditado", new CatalogItemChanges(Name: "Depois"), stage.RowVersion, human, Ct);
        await host.Admin.CreateStageAsync(new NewCatalogItem("automatico", "Automatico"), Actor.System("queue"), Ct);
        await host.PublishStageAsync("eco", Echo, [Text("texto")], [Text("texto")]);
        var auditedBefore = await host.ScalarAsync<long>("SELECT count(*) FROM psr.audit_events");
        await using (var result = await host.Runner.RunStageAsync("eco", new { texto = "lib" }, cancellationToken: Ct))
        {
            result.EnsureSuccess();
        }

        var events = await host.Services.GetRequiredService<IAuditReader>().ListAsync(new AuditQuery(), Ct);

        created.Version.ShouldBe(1);
        events.Items.ShouldAllBe(e => e.Actor == human.Subject);
        events.Items.ShouldNotContain(e => e.EntityKey == "automatico");
        var update = events.Items.Single(e => e.Action == AuditActions.StageUpdated);
        update.Details!["name"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["Antes", "Depois"]);
        (await host.ScalarAsync<long>("SELECT count(*) FROM psr.audit_events")).ShouldBe(auditedBefore, "library runs are recorded on the run, not audited");
    }

    [Fact]
    public async Task Spec003_AC10_ReprocessReusesSucceededStepsAndRetriesTheFailedOne()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python);
        var marker = Path.Combine(Path.GetTempPath(), $"psr-marcador-{Guid.NewGuid():N}");
        await host.PublishStageAsync("eco", Echo, [Text("texto")], [Text("texto")]);
        await host.PublishStageAsync("falha-uma-vez", FailOnce, [Text("texto"), Text("marcador")], [Text("texto")]);
        await host.PublishPipelineAsync("reprocesso", new PipelineDraftData
        {
            Inputs = [Text("texto")],
            Steps =
            [
                new PipelineStepDefinition("eco", "eco", 1),
                new PipelineStepDefinition("falha", "falha-uma-vez", 1) { Bindings = [new InputBinding("marcador", From.Constant(marker))] },
            ],
            Outputs = [new PipelineOutputDefinition("texto", From.Step("falha", "texto"))],
        });

        try
        {
            RunId runId;
            await using (var failed = await host.Runner.RunPipelineAsync("reprocesso", new { texto = "abc" }, cancellationToken: Ct))
            {
                failed.Status.ShouldBe(RunStatus.Failed);
                failed.Error!.StepKey.ShouldBe("falha");
                runId = failed.Id;
            }

            var reprocessed = await host.Runs.ReprocessAsync(runId, Actor.User("operador@teste"), ExecutionMode.Sync, Ct);

            reprocessed.Id.ShouldBe(runId);
            reprocessed.Status.ShouldBe(RunStatus.Succeeded);
            reprocessed.Outputs!["texto"]!.GetValue<string>().ShouldBe("ABC");
            reprocessed.Steps[0].Attempts.ShouldHaveSingleItem("the succeeded step is not executed again");
            reprocessed.Steps[1].Attempts.Select(a => (a.Number, a.Cause)).ShouldBe([(1, AttemptCause.Initial), (2, AttemptCause.Reprocess)]);
            reprocessed.Steps[1].Attempts[1].TriggeredBy.ShouldBe("operador@teste");
            (await Should.ThrowAsync<PsrDomainException>(() => host.Runs.ReprocessAsync(runId, PsrTestHost.Author, ExecutionMode.Sync, Ct)))
                .Code.ShouldBe(PsrErrorCodes.RunNotFailed);
            (await host.ScalarAsync<long>("SELECT count(*) FROM psr.audit_events WHERE action = 'RunReprocessed'")).ShouldBe(1);
        }
        finally
        {
            System.IO.File.Delete(marker);
        }
    }

    [Fact]
    public async Task Spec003_AC14_PipelineDraftIsTestedWithoutPublishing()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python);
        await host.PublishStageAsync("eco", Echo, [Text("texto")], [Text("texto")]);
        var draft = await host.Admin.CreatePipelineAsync(new NewCatalogItem("rascunho", "Rascunho"), PsrTestHost.Author, Ct);
        await host.Admin.SavePipelineDraftAsync("rascunho", 1, new PipelineDraftData
        {
            Inputs = [Text("texto")],
            Steps = [new PipelineStepDefinition("eco", "eco", 1)],
            Outputs = [new PipelineOutputDefinition("texto", From.Step("eco", "texto"))],
        }, draft.RowVersion, PsrTestHost.Author, Ct);
        var invalid = await host.Admin.CreatePipelineAsync(new NewCatalogItem("invalido", "Invalido"), PsrTestHost.Author, Ct);
        await host.Admin.SavePipelineDraftAsync("invalido", 1, new PipelineDraftData { Steps = [new PipelineStepDefinition("eco", "eco", 1)] },
            invalid.RowVersion, PsrTestHost.Author, Ct);

        await using var result = await host.Admin.TestPipelineDraftAsync("rascunho", new { texto = "teste" }, PsrTestHost.Author, Ct);
        var error = await Should.ThrowAsync<DefinitionInvalidException>(() => host.Admin.TestPipelineDraftAsync("invalido", new { }, PsrTestHost.Author, Ct));

        result.Kind.ShouldBe(RunKind.PipelineTest);
        result.GetOutput<string>("texto").ShouldBe("teste");
        var run = (await host.Runs.GetAsync(result.Id, Ct))!;
        run.Kind.ShouldBe(RunKind.PipelineTest);
        run.TargetVersion.ShouldBe(1);
        run.Trigger.ShouldBe(RunTrigger.Dashboard);
        (await host.Admin.GetPipelineAsync("rascunho", Ct))!.LatestPublishedVersion.ShouldBeNull();
        (await host.Admin.GetPipelineDraftAsync("rascunho", Ct)).ShouldNotBeNull();
        await using (var db = await host.DbAsync())
        {
            var row = await db.Runs.SingleAsync(r => r.Id == result.Id.Value, Ct);
            var draftVersion = await db.PipelineVersions.SingleAsync(v => v.Pipeline.Key == "rascunho", Ct);
            row.PipelineVersionId.ShouldBe(draftVersion.Id);
        }

        error.Code.ShouldBe(PsrErrorCodes.PipelineInvalid);
        error.Report.Errors.ShouldContain(e => e.Code == "PIPE_UNBOUND_REQUIRED" && e.Step == "eco" && e.Field == "texto");
    }

    [Fact]
    public async Task CancellingAFinishedRunIsRejected()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python);
        var finished = await InsertRunAsync(host, DateTimeOffset.UtcNow, RunStatus.Succeeded);
        var queued = await InsertRunAsync(host, DateTimeOffset.UtcNow, RunStatus.Queued);

        var error = await Should.ThrowAsync<PsrDomainException>(() => host.Runs.CancelAsync(finished, PsrTestHost.Author, Ct));
        await host.Runs.CancelAsync(queued, PsrTestHost.Author, Ct);

        error.Code.ShouldBe(PsrErrorCodes.RunNotCancellable);
        var cancelled = (await host.Runs.GetAsync(queued, Ct))!;
        cancelled.Status.ShouldBe(RunStatus.Cancelled);
        cancelled.CancelRequestedBy.ShouldBe(PsrTestHost.Author.Subject);
    }

    internal static RunEntity Run(RunId id, RunStatus status, string? stageKey = null, Guid? pipelineId = null) => new()
    {
        Id = id.Value,
        CreatedAt = id.CreatedAt,
        Kind = pipelineId is null ? RunKind.Stage : RunKind.Pipeline,
        Mode = ExecutionMode.Async,
        StageKey = stageKey,
        StageVersion = stageKey is null ? null : 1,
        PipelineId = pipelineId,
        Trigger = RunTrigger.Library,
        TriggeredBy = "system:library",
        Status = status,
        Inputs = "{}",
    };

    internal static async Task<RunId> InsertRunAsync(PsrTestHost host, DateTimeOffset createdAt, RunStatus status, Guid? pipelineId = null)
    {
        var id = RunId.New(createdAt);
        await using var db = await host.DbAsync();
        db.Runs.Add(Run(id, status, stageKey: pipelineId is null ? "manual" : null, pipelineId: pipelineId));
        await db.SaveChangesAsync(Ct);
        return id;
    }
}
