using Microsoft.Extensions.Time.Testing;
using Npgsql;
using PythonScriptRunner.EntityFrameworkCore.Tests.Infrastructure;
using static PythonScriptRunner.EntityFrameworkCore.Tests.Infrastructure.TestScripts;

namespace PythonScriptRunner.EntityFrameworkCore.Tests;

public sealed class MaintenanceTests(PostgresFixture postgres, PythonFixture python)
{
    private const string Partitions = """
        SELECT child.relname::text FROM pg_inherits inh
        JOIN pg_class child ON child.oid = inh.inhrelid JOIN pg_class parent ON parent.oid = inh.inhparent
        JOIN pg_namespace ns ON ns.oid = parent.relnamespace
        WHERE ns.nspname = 'psr' AND parent.relkind = 'p' AND child.relkind = 'r'
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spec004_AC14_CreatesPartitionsAheadAndDropsThoseBeyondTheRetention()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2030, 1, 7, 12, 0, 0, TimeSpan.Zero));
        await using var host = await PsrTestHost.CreateAsync(postgres, python, time);

        var first = await host.Maintenance.RunOnceAsync(Ct);
        var created = await host.ColumnAsync<string>(Partitions);
        time.Advance(TimeSpan.FromDays(100));
        var later = await host.Maintenance.RunOnceAsync(Ct);
        var remaining = await host.ColumnAsync<string>(Partitions);

        first.ShouldNotBeNull().PartitionsCreated.ShouldBe(5 * 5 + 8 + 4);
        created.ShouldContain("runs_p20300107");
        created.ShouldContain("runs_p20300204");
        created.ShouldContain("artifacts_p20300204");
        created.ShouldContain("artifact_contents_p20300114");
        created.ShouldContain("audit_events_p203004");

        later.ShouldNotBeNull();
        later.PartitionsDropped.ShouldContain("runs_p20300107");
        later.PartitionsDropped.ShouldContain("attempt_logs_p20300107");
        later.PartitionsDropped.ShouldContain("artifact_contents_p20300107");
        remaining.ShouldNotContain("runs_p20300107");
        remaining.ShouldContain("runs_p20300114", "partitions inside the 90-day retention are kept");
        remaining.ShouldContain("audit_events_p203001", "audit is kept for a year");
        remaining.ShouldContain("runs_p20300415");
        remaining.ShouldContain("runs_p20300513");
        remaining.ShouldContain("artifact_contents_p20300424");
    }

    [Fact]
    public async Task Spec004_AC15_ShorterPipelineRetentionDeletesFileContentsButKeepsMetadata()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = await PsrTestHost.CreateAsync(postgres, python, time);
        await host.PublishStageAsync("gera-arquivo", WriteFile, [Text("texto")], [File("arquivo"), new FieldDefinition("tamanho", FieldType.Integer)]);
        await host.PublishPipelineAsync("curta", new PipelineDraftData
        {
            Inputs = [Text("texto")],
            Steps = [new PipelineStepDefinition("gera", "gera-arquivo", 1)],
            Outputs = [new PipelineOutputDefinition("arquivo", From.Step("gera", "arquivo"))],
        });
        var pipeline = (await host.Admin.GetPipelineAsync("curta", Ct))!;
        await host.Admin.UpdatePipelineRetentionAsync("curta", new PipelineRetention(2, null), pipeline.RowVersion, PsrTestHost.Author, Ct);
        RunId runId;
        await using (var result = await host.Runner.RunPipelineAsync("curta", new { texto = "z" }, cancellationToken: Ct))
        {
            result.EnsureSuccess();
            runId = result.Id;
        }

        var artifactId = (await host.Runs.GetAsync(runId, Ct))!.Artifacts.Single().Id;
        time.Advance(TimeSpan.FromDays(1));
        await host.Maintenance.RunOnceAsync(Ct);
        await using (var stillThere = await host.Artifacts.OpenReadAsync(artifactId, Ct))
        {
            (await new StreamReader(stillThere).ReadToEndAsync(Ct)).ShouldBe("zzz");
        }

        time.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromMinutes(1)));
        var report = await host.Maintenance.RunOnceAsync(Ct);

        report.ShouldNotBeNull().ContentsDeleted.ShouldBe(1);
        var info = await host.Artifacts.GetAsync(artifactId, Ct);
        info.ShouldNotBeNull().ContentDeletedAt.ShouldNotBeNull();
        (await host.ScalarAsync<long>("SELECT count(*) FROM psr.artifact_contents")).ShouldBe(0);
        (await Should.ThrowAsync<PsrDomainException>(() => host.Artifacts.OpenReadAsync(artifactId, Ct))).Code.ShouldBe(PsrErrorCodes.ArtifactExpired);
        (await host.Runs.GetAsync(runId, Ct)).ShouldNotBeNull("run metadata stays until the run retention");
    }

    [Fact]
    public async Task Spec004_AC19_UnfinishedRunsEndExpiredBeforeTheirFilesAreDropped()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = await PsrTestHost.CreateAsync(postgres, python, time);
        var old = await RunHistoryTests.InsertRunAsync(host, time.GetUtcNow(), RunStatus.Queued);
        time.Advance(TimeSpan.FromDays(1));
        var recent = await RunHistoryTests.InsertRunAsync(host, time.GetUtcNow(), RunStatus.Queued);

        time.Advance(TimeSpan.FromDays(5));
        var report = await host.Maintenance.RunOnceAsync(Ct);

        report.ShouldNotBeNull().RunsExpired.ShouldBe(1);
        var expired = (await host.Runs.GetAsync(old, Ct))!;
        expired.Status.ShouldBe(RunStatus.Failed);
        expired.Error!.Kind.ShouldBe(ErrorKind.Expired);
        (await host.Runs.GetAsync(recent, Ct))!.Status.ShouldBe(RunStatus.Queued, "5 days old is still inside retention minus one day");
    }

    [Fact]
    public async Task Spec004_AC20_OnlyOneInstanceRunsEachCycle()
    {
        await using var first = await PsrTestHost.CreateAsync(postgres, python);
        await using var holder = new NpgsqlConnection(first.ConnectionString);
        await holder.OpenAsync(Ct);
        await using (var acquire = new NpgsqlCommand($"SELECT pg_advisory_lock({PsrMaintenance.LockKey})", holder))
        {
            await acquire.ExecuteScalarAsync(Ct);
        }

        var whileHeld = await first.Maintenance.RunOnceAsync(Ct);
        await using (var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({PsrMaintenance.LockKey})", holder))
        {
            await release.ExecuteScalarAsync(Ct);
        }

        var afterRelease = await first.Maintenance.RunOnceAsync(Ct);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => first.Maintenance.RunOnceAsync(Ct)));

        whileHeld.ShouldBeNull();
        afterRelease.ShouldNotBeNull();
        concurrent.Count(r => r is not null).ShouldBeGreaterThanOrEqualTo(1);
    }
}
