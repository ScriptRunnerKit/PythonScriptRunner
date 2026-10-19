using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using PythonScriptRunner.Core.Tests.Infrastructure;

namespace PythonScriptRunner.Core.Tests.Integration;

public sealed class RetryTests(PythonFixture fixture)
{
    private const string FailsTwice = """
        from pathlib import Path
        from psrunner import StageError, stage

        @stage
        def run(inputs, ctx):
            counter = Path(inputs["contador"])
            count = int(counter.read_text()) + 1 if counter.exists() else 1
            counter.write_text(str(count))
            if count < 3:
                raise StageError(f"tentativa {count} falhou")
            return {"tentativas": count}
        """;

    [Fact]
    public async Task Spec001_AC21_RetriesWithBackoff_UntilSuccess()
    {
        // Arrange
        var counter = Path.Combine(Path.GetTempPath(), "psr-tests", $"counter-{Guid.NewGuid():N}.txt");
        await using var host = RunnerHost.Create(fixture, b => b.AddStage("instavel", s => s
            .Script(FailsTwice)
            .Input("contador", FieldType.String)
            .Output("tentativas", FieldType.Integer)
            .Retry(2, TimeSpan.FromMilliseconds(200), 2.0)));

        // Act
        await using var result = await host.Runner.RunStageAsync("instavel", new { contador = counter }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Status.ShouldBe(RunStatus.Succeeded, result.Error?.Message);
        result.GetOutput<int>("tentativas").ShouldBe(3);
        var attempts = result.Steps[0].Attempts;
        attempts.Select(a => (a.Number, a.Cause, a.Status)).ShouldBe(
        [
            (1, AttemptCause.Initial, AttemptStatus.Failed),
            (2, AttemptCause.AutoRetry, AttemptStatus.Failed),
            (3, AttemptCause.AutoRetry, AttemptStatus.Succeeded),
        ]);
        (attempts[1].StartedAt - (attempts[0].StartedAt + attempts[0].Duration)).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(190));
        (attempts[2].StartedAt - (attempts[1].StartedAt + attempts[1].Duration)).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(390));
    }

    [Fact]
    public async Task Spec001_AC22_WithoutRetry_ThereIsOneAttempt()
    {
        var counter = Path.Combine(Path.GetTempPath(), "psr-tests", $"counter-{Guid.NewGuid():N}.txt");
        await using var host = RunnerHost.Create(fixture, b => b.AddStage("instavel", s => s
            .Script(FailsTwice)
            .Input("contador", FieldType.String)
            .Output("tentativas", FieldType.Integer)));

        await using var result = await host.Runner.RunStageAsync("instavel", new { contador = counter }, cancellationToken: TestContext.Current.CancellationToken);

        result.Status.ShouldBe(RunStatus.Failed);
        result.Steps[0].Attempts.ShouldHaveSingleItem();
    }
}

public sealed class TelemetryTests(PythonFixture fixture)
{
    [Fact]
    public async Task Spec001_AC24_EmitsActivityAndMetrics()
    {
        // Arrange
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "PythonScriptRunner",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (activities)
                {
                    activities.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        await using var host = RunnerHost.Create(fixture, b => b.AddStage("telemetria", s => s.Script(Scripts.Echo).Input("texto", FieldType.String).Output("texto", FieldType.String)));
        using var durations = new MetricCollector<double>(host.Services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(), "PythonScriptRunner", "psr.step.duration");

        // Act
        await using var result = await host.Runner.RunStageAsync("telemetria", new { texto = "x" }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Activity execute;
        lock (activities)
        {
            execute = activities.Single(a => a.OperationName == "psr.stage.execute" && Equals(a.GetTagItem("psr.run.id"), result.Id.ToString()));
        }

        execute.GetTagItem("psr.stage.key").ShouldBe("telemetria");
        execute.GetTagItem("psr.stage.version").ShouldBe(1);
        execute.GetTagItem("psr.step.key").ShouldBe("telemetria");
        execute.GetTagItem("psr.attempt").ShouldBe(1);
        var measurement = durations.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Tags["psr.stage.key"].ShouldBe("telemetria");
        measurement.Tags["psr.outcome"].ShouldBe("Succeeded");
    }
}
