using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace PythonScriptRunner.Worker.Tests;

/// <summary>Spec 006: cron schedules (D-41, D-44, D-45, D-77, D-89) and the schedule editor (D-101).</summary>
public sealed class SchedulerTests(PostgresFixture postgres, BrokerFixture broker, PythonFixture python)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spec006_AC10_TwoSchedulers_FireEachOccurrenceOnce()
    {
        var time = new FakeTimeProvider(HalfPastTheMinute());
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python, time);
        var first = await env.StartHostAsync(s => s.Scheduler = true);
        await env.StartHostAsync(s => s.Scheduler = true);
        var schedule = await CreateEchoScheduleAsync(first, "*/1 * * * *");

        for (var minute = 1; minute <= 5; minute++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            var expected = minute;
            await Eventually.WaitAsync(async () => await CountRunsAsync(env, schedule) >= expected, TimeSpan.FromSeconds(20));
        }

        time.Advance(TimeSpan.FromSeconds(20));
        await Task.Delay(2000, Ct);
        (await CountRunsAsync(env, schedule)).ShouldBe(5);
        (await first.Admin.GetScheduleAsync(schedule.Id, Ct))!.LastRunId.ShouldNotBeNull();
    }

    [Fact]
    public async Task Spec006_AC11_MissedOccurrences_BecomeOneRecoveryRun()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python, time);
        var app = await env.StartHostAsync(s => s.Worker = false);
        var schedule = await CreateEchoScheduleAsync(app, "*/1 * * * *");

        // Every worker is down for 10 occurrences.
        time.SetUtcNow(schedule.NextRunAt!.Value + TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(30));
        await env.StartHostAsync(s => s.Scheduler = true);
        await Eventually.WaitAsync(async () => await CountRunsAsync(env, schedule) >= 1, TimeSpan.FromSeconds(20));
        await Task.Delay(1500, Ct);

        (await CountRunsAsync(env, schedule)).ShouldBe(1);
        var runId = (await app.Admin.GetScheduleAsync(schedule.Id, Ct))!.LastRunId!.Value;
        var run = (await app.Runs.GetAsync(runId, Ct))!;
        run.Trigger.ShouldBe(RunTrigger.Schedule);
        run.TriggeredBy.ShouldBe($"system:schedule:{schedule.Id}");
        run.ScheduleId.ShouldBe(schedule.Id);
        run.TriggerDetails!["missedOccurrences"]!.GetValue<int>().ShouldBe(10);
    }

    [Fact]
    public async Task Spec006_AC12_LatestVersion_FollowsPublicationsAndPinnedVersionDoesNot()
    {
        var time = new FakeTimeProvider(HalfPastTheMinute());
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python, time);
        var host = await env.StartHostAsync(s => s.Scheduler = true);
        var latest = await CreateEchoScheduleAsync(host, "*/1 * * * *");
        var pinned = await host.Admin.CreateScheduleAsync("eco-agendado", EchoSchedule("*/1 * * * *") with { Name = "fixa", PinnedVersion = 1 },
            PsrEnvironment.Author, Ct);
        await PsrEnvironment.PublishPipelineAsync(host, "eco-agendado", Scripts.SingleStep("eco", 1, [Scripts.Text("texto")]));

        time.Advance(TimeSpan.FromMinutes(1));
        await Eventually.WaitAsync(async () => await CountRunsAsync(env, latest) == 1 && await CountRunsAsync(env, pinned) == 1, TimeSpan.FromSeconds(20));

        (await env.ScalarAsync<int>($"SELECT pipeline_version FROM psr.runs WHERE schedule_id = '{latest.Id}'")).ShouldBe(2);
        (await env.ScalarAsync<int>($"SELECT pipeline_version FROM psr.runs WHERE schedule_id = '{pinned.Id}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Spec006_AC13_NextRun_IsComputedInTheTimeZone()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python, time);
        var app = await env.StartHostAsync(s => s.Worker = false);
        await PublishEchoPipelineAsync(app);

        var schedule = await app.Admin.CreateScheduleAsync("eco-agendado", EchoSchedule("0 2 * * *"), PsrEnvironment.Author, Ct);

        schedule.Data.TimeZone.ShouldBe("America/Sao_Paulo");
        schedule.NextRunAt.ShouldBe(new DateTimeOffset(2026, 10, 21, 5, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Spec006_AC14_PipelineWithFileInput_CannotBeScheduled()
    {
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python);
        var app = await env.StartHostAsync(s => s.Worker = false);
        await PsrEnvironment.PublishStageAsync(app, "le-arquivo", "from psrunner import stage\n\n\n@stage\ndef run(inputs, ctx):\n    return {}\n",
            [Scripts.File("arquivo")], []);
        await PsrEnvironment.PublishPipelineAsync(app, "com-arquivo", new PipelineDraftData
        {
            Inputs = [Scripts.File("arquivo")],
            Steps = [new PipelineStepDefinition("le", "le-arquivo", 1)],
        });

        var error = await Should.ThrowAsync<PsrDomainException>(() =>
            app.Admin.CreateScheduleAsync("com-arquivo", new ScheduleData { Name = "x", Cron = "0 * * * *" }, PsrEnvironment.Author, Ct));

        error.Code.ShouldBe(PsrErrorCodes.ScheduleFileInput);
    }

    [Fact]
    public async Task ScheduleEditor_ValidatesAuditsAndRecomputesTheNextRun()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 30, TimeSpan.Zero));
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python, time);
        var app = await env.StartHostAsync(s => s.Worker = false);
        await PublishEchoPipelineAsync(app);

        (await Should.ThrowAsync<PsrDomainException>(() => app.Admin.CreateScheduleAsync("eco-agendado", EchoSchedule("61 * * * *"), PsrEnvironment.Author, Ct)))
            .Code.ShouldBe(PsrErrorCodes.InvalidCron);
        (await Should.ThrowAsync<PsrDomainException>(() =>
                app.Admin.CreateScheduleAsync("eco-agendado", EchoSchedule("* * * * *") with { TimeZone = "Marte/Olimpo" }, PsrEnvironment.Author, Ct)))
            .Code.ShouldBe(PsrErrorCodes.InvalidTimeZone);
        await Should.ThrowAsync<InputValidationException>(() =>
            app.Admin.CreateScheduleAsync("eco-agendado", EchoSchedule("* * * * *") with { Inputs = new JsonObject { ["outro"] = 1 } }, PsrEnvironment.Author, Ct));
        await Should.ThrowAsync<PipelineNotFoundException>(() => app.Admin.CreateScheduleAsync("nao-existe", EchoSchedule("* * * * *"), PsrEnvironment.Author, Ct));

        var created = await app.Admin.CreateScheduleAsync("eco-agendado", EchoSchedule("*/5 * * * *"), PsrEnvironment.Author, Ct);
        created.NextRunAt.ShouldBe(new DateTimeOffset(2026, 10, 20, 12, 5, 0, TimeSpan.Zero));
        var updated = await app.Admin.UpdateScheduleAsync(created.Id, created.Data with { Cron = "0 * * * *" }, created.RowVersion, PsrEnvironment.Author, Ct);
        updated.NextRunAt.ShouldBe(new DateTimeOffset(2026, 10, 20, 13, 0, 0, TimeSpan.Zero));
        (await Should.ThrowAsync<ConcurrencyConflictException>(() =>
            app.Admin.UpdateScheduleAsync(created.Id, created.Data, created.RowVersion, PsrEnvironment.Author, Ct))).Current.ShouldNotBeNull();
        var disabled = await app.Admin.SetScheduleEnabledAsync(created.Id, false, updated.RowVersion, PsrEnvironment.Author, Ct);
        disabled.NextRunAt.ShouldBeNull();
        var enabled = await app.Admin.SetScheduleEnabledAsync(created.Id, true, disabled.RowVersion, PsrEnvironment.Author, Ct);
        enabled.NextRunAt.ShouldBe(new DateTimeOffset(2026, 10, 20, 13, 0, 0, TimeSpan.Zero));
        (await app.Admin.ListSchedulesAsync(new ScheduleQuery("eco-agendado"), Ct)).Items.Select(s => s.Id).ShouldBe([created.Id]);
        await app.Admin.DeleteScheduleAsync(created.Id, PsrEnvironment.Author, Ct);
        (await app.Admin.GetScheduleAsync(created.Id, Ct)).ShouldBeNull();

        // The simulated clock does not move, so the events share one instant.
        var actions = await env.ColumnAsync<string>($"SELECT action FROM psr.audit_events WHERE entity_id = '{created.Id}'");
        actions.ShouldBe(["ScheduleCreated", "ScheduleUpdated", "ScheduleDisabled", "ScheduleEnabled", "ScheduleDeleted"], ignoreOrder: true);
        (await env.ScalarAsync<string>($"SELECT details->>'cron' FROM psr.audit_events WHERE entity_id = '{created.Id}' AND action = 'ScheduleUpdated'"))
            .ShouldBe("""["*/5 * * * *", "0 * * * *"]""");
    }

    [Fact]
    public async Task NewRequiredInput_WarnsOnPublicationAndRejectsTheFiringButKeepsTheSchedule()
    {
        var time = new FakeTimeProvider(HalfPastTheMinute());
        await using var env = await PsrEnvironment.CreateAsync(postgres, broker, python, time);
        var host = await env.StartHostAsync(s => s.Scheduler = true);
        var schedule = await CreateEchoScheduleAsync(host, "*/1 * * * *");
        await PsrEnvironment.PublishStageAsync(host, "eco2", Scripts.Echo, [Scripts.Text("texto"), Scripts.Text("obrigatoria")], [Scripts.Text("texto")]);

        var publication = await PsrEnvironment.PublishPipelineAsync(host, "eco-agendado",
            Scripts.SingleStep("eco2", 1, [Scripts.Text("texto"), Scripts.Text("obrigatoria")]));
        publication.Warnings.ShouldContain(w => w.Code == "SCHEDULE_MISSING_INPUT" && w.Message.Contains("obrigatoria", StringComparison.Ordinal));

        time.Advance(TimeSpan.FromMinutes(1));
        var rejection = await host.Events.WaitForAsync(RunEventTypes.RunRejected, e => e.PipelineKey == "eco-agendado");

        rejection.Error!.Reasons!.ShouldContain(r => r.Contains("obrigatoria", StringComparison.Ordinal));
        rejection.TriggerDetails!["scheduleId"]!.GetValue<string>().ShouldBe(schedule.Id.ToString());
        var stored = (await host.Admin.GetScheduleAsync(schedule.Id, Ct))!;
        stored.Data.IsEnabled.ShouldBeTrue();
        stored.LastError.ShouldNotBeNull();
        stored.NextRunAt.ShouldNotBeNull();
        (await CountRunsAsync(env, schedule)).ShouldBe(0);
    }

    /// <summary>Now, moved to second 30 of its minute: each advance of one minute then crosses exactly one occurrence of "*/1".</summary>
    private static DateTimeOffset HalfPastTheMinute()
    {
        var now = DateTimeOffset.UtcNow;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMinute)).AddSeconds(30);
    }

    private static ScheduleData EchoSchedule(string cron) => new() { Name = "eco", Cron = cron, Inputs = new JsonObject { ["texto"] = "agendado" } };

    private static async Task PublishEchoPipelineAsync(PsrHost host)
    {
        await PsrEnvironment.PublishStageAsync(host, "eco", Scripts.Echo, [Scripts.Text("texto")], [Scripts.Text("texto")]);
        await PsrEnvironment.PublishPipelineAsync(host, "eco-agendado", Scripts.SingleStep("eco", 1, [Scripts.Text("texto")]));
    }

    private static async Task<ScheduleDetail> CreateEchoScheduleAsync(PsrHost host, string cron)
    {
        await PublishEchoPipelineAsync(host);
        return await host.Admin.CreateScheduleAsync("eco-agendado", EchoSchedule(cron), PsrEnvironment.Author, Ct);
    }

    private static Task<long> CountRunsAsync(PsrEnvironment env, ScheduleDetail schedule) =>
        env.ScalarAsync<long>($"SELECT count(*) FROM psr.runs WHERE schedule_id = '{schedule.Id}'");
}
