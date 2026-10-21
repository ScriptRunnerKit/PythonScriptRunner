using System.Collections.Concurrent;
using System.Text;
using DotNet.Testcontainers.Configurations;

namespace PythonScriptRunner.Containers.Tests.Infrastructure;

/// <summary>
/// The output of the containers, captured while they run: a test removes its containers when it ends, and their logs
/// with them. When a test fails, <see cref="ReportAsync"/> saves every log to <c>TestResults/container-logs/&lt;test&gt;/</c>
/// next to the test assembly (the CI uploads that folder) and writes the end of the host logs to the test output.
/// </summary>
public static class ContainerLogs
{
    private const int TailLines = 80;
    private const string SharedKey = "";

    private static readonly ConcurrentDictionary<string, ConcurrentQueue<Entry>> Logs = new();

    /// <summary>An output consumer that keeps what the container writes, for the current test.</summary>
    /// <param name="name">File name of the log (made unique within the test).</param>
    /// <param name="host">Whether the end of the log also goes to the test output (the worker and the dashboard).</param>
    public static IOutputConsumer Capture(string name, bool host = true) => Capture(CurrentTest(), name, host);

    /// <summary>An output consumer for a container shared by every test (PostgreSQL, RabbitMQ).</summary>
    public static IOutputConsumer CaptureShared(string name) => Capture(SharedKey, name, host: false);

    /// <summary>Adds a log read some other way (<c>docker compose logs</c>) to the current test.</summary>
    public static void Add(string name, string text, bool host = true) => Register(CurrentTest(), name, () => text, host);

    /// <summary>Called when a test class is disposed: writes the logs if the test failed and forgets them.</summary>
    public static async Task ReportAsync()
    {
        if (TestContext.Current.Test is not { } test)
        {
            return;
        }

        Logs.TryRemove(test.UniqueID, out var own);
        if (TestContext.Current.TestState?.Result != TestResult.Failed)
        {
            return;
        }

        var name = TestContext.Current.TestMethod?.MethodName ?? test.UniqueID;
        var folder = Path.Combine(AppContext.BaseDirectory, "TestResults", "container-logs", name);
        Directory.CreateDirectory(folder);
        var output = TestContext.Current.TestOutputHelper;
        var shared = Logs.TryGetValue(SharedKey, out var common) ? common : [];
        foreach (var entry in (own ?? []).Concat(shared))
        {
            var text = entry.Read();
            await File.WriteAllTextAsync(Path.Combine(folder, $"{entry.Name}.log"), text);
            if (entry.Host && output is not null)
            {
                var lines = text.Split('\n');
                output.WriteLine($"----- {entry.Name}: last {Math.Min(TailLines, lines.Length)} of {lines.Length} lines -----");
                output.WriteLine(string.Join('\n', lines.TakeLast(TailLines)));
            }
        }

        output?.WriteLine($"Container logs: {folder}");
    }

    private static IOutputConsumer Capture(string key, string name, bool host)
    {
        var stdout = new LogStream();
        var stderr = new LogStream();
        Register(key, name, () => stderr.Length == 0 ? stdout.Text() : $"{stdout.Text()}\n----- stderr -----\n{stderr.Text()}", host);
        return Consume.RedirectStdoutAndStderrToStream(stdout, stderr);
    }

    private static void Register(string key, string name, Func<string> read, bool host)
    {
        var entries = Logs.GetOrAdd(key, _ => new ConcurrentQueue<Entry>());
        lock (entries)
        {
            var unique = name;
            for (var i = 2; entries.Any(e => e.Name == unique); i++)
            {
                unique = $"{name}-{i}";
            }

            entries.Enqueue(new Entry(unique, read, host));
        }
    }

    private static string CurrentTest() =>
        TestContext.Current.Test?.UniqueID ?? throw new InvalidOperationException("Container logs are captured inside a test.");

    private sealed record Entry(string Name, Func<string> Read, bool Host);

    /// <summary>A write-only buffer that Testcontainers fills from a background task and that is read after the container is gone.</summary>
    private sealed class LogStream : Stream
    {
        private readonly MemoryStream _buffer = new();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length
        {
            get
            {
                lock (_buffer)
                {
                    return _buffer.Length;
                }
            }
        }

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public string Text()
        {
            lock (_buffer)
            {
                return Encoding.UTF8.GetString(_buffer.GetBuffer(), 0, (int)_buffer.Length);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_buffer)
            {
                _buffer.Write(buffer, offset, count);
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        // Testcontainers disposes the consumer with the container; the captured text stays readable.
        protected override void Dispose(bool disposing)
        {
        }
    }
}
