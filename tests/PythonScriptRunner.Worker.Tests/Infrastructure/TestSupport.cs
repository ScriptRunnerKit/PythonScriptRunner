using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PythonScriptRunner.Testing;

/// <summary>Collects the run events delivered to a host (registered as a per-instance subscription).</summary>
public sealed class EventRecorder : IRunEventHandler
{
    private readonly ConcurrentQueue<RunEvent> _events = new();

    public IReadOnlyList<RunEvent> All => [.. _events];

    public Task HandleAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
        _events.Enqueue(runEvent);
        return Task.CompletedTask;
    }

    public IReadOnlyList<RunEvent> For(RunId runId) => [.. _events.Where(e => e.RunId == runId)];

    /// <summary>Waits for an event of <paramref name="type"/> matching <paramref name="match"/>.</summary>
    public async Task<RunEvent> WaitForAsync(string type, Func<RunEvent, bool>? match = null, TimeSpan? timeout = null)
    {
        RunEvent? found = null;
        await Eventually.WaitAsync(() =>
        {
            found = _events.FirstOrDefault(e => e.Type == type && (match is null || match(e)));
            return Task.FromResult(found is not null);
        }, timeout ?? TimeSpan.FromSeconds(30), () => $"no {type} event; received: {string.Join(", ", _events.Select(e => e.Type))}");
        return found!;
    }
}

/// <summary>Polls a condition.</summary>
public static class Eventually
{
    public static async Task WaitAsync(Func<Task<bool>> condition, TimeSpan timeout, Func<string>? describe = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not met in {timeout}: {describe?.Invoke()}");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>Scripts and fields shared by the queue and worker tests.</summary>
public static class Scripts
{
    public const string Echo = """
        from psrunner import stage


        @stage
        def run(inputs, ctx):
            return {"texto": inputs["texto"]}
        """;

    /// <summary>Sleeps <c>segundos</c> and reports when it ran.</summary>
    public const string Sleep = """
        import time

        from psrunner import stage


        @stage
        def run(inputs, ctx):
            time.sleep(inputs["segundos"])
            return {"texto": inputs["texto"]}
        """;

    /// <summary>Appends one line to <c>marcador</c> per execution and returns the count.</summary>
    public const string Count = """
        from psrunner import stage


        @stage
        def run(inputs, ctx):
            with open(inputs["marcador"], "a", encoding="utf-8") as marker:
                marker.write("x\n")
            with open(inputs["marcador"], encoding="utf-8") as marker:
                return {"execucoes": len(marker.readlines())}
        """;

    /// <summary>Sleeps a long time on the first attempt only (the worker is killed meanwhile).</summary>
    public const string SlowFirstAttempt = """
        import time

        from psrunner import stage


        @stage
        def run(inputs, ctx):
            if ctx.attempt == 1:
                time.sleep(120)
            return {"tentativa": ctx.attempt}
        """;

    public const string AlwaysFails = """
        from psrunner import StageError, stage


        @stage
        def run(inputs, ctx):
            raise StageError("sempre falha", code="SEMPRE")
        """;

    public static FieldDefinition Text(string name) => new(name, FieldType.String);

    public static FieldDefinition Number(string name) => new(name, FieldType.Number);

    public static FieldDefinition Integer(string name) => new(name, FieldType.Integer);

    public static FieldDefinition File(string name) => new(name, FieldType.File);

    /// <summary>A pipeline with one step running <paramref name="stageKey"/>@<paramref name="stageVersion"/> (inputs and outputs by name).</summary>
    public static PipelineDraftData SingleStep(string stageKey, int stageVersion, IReadOnlyList<FieldDefinition> inputs, string output = "texto") => new()
    {
        Inputs = inputs,
        Steps = [new PipelineStepDefinition("passo", stageKey, stageVersion)],
        Outputs = [new PipelineOutputDefinition(output, new StepOutputSource("passo", output))],
    };
}

/// <summary>Appends host logs to the file named by <c>PSR_TEST_LOG</c> (diagnostics of failing tests).</summary>
public sealed class TestFileLoggerProvider(string path, string instance) : ILoggerProvider
{
    private static readonly Lock Gate = new();

    public ILogger CreateLogger(string categoryName) => new Logger(path, instance, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(string path, string instance, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Gate)
            {
                File.AppendAllText(path, $"[{DateTime.UtcNow:HH:mm:ss.fff} {instance} {logLevel} {category}] {formatter(state, exception)} {exception}{Environment.NewLine}");
            }
        }
    }
}
