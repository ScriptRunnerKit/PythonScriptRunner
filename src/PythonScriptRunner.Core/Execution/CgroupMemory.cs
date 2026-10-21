using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PythonScriptRunner.Core.Diagnostics;

namespace PythonScriptRunner.Core.Execution;

/// <summary>
/// Memory limits of Python processes with cgroup v2 on Linux (spec 001, D-180).
/// </summary>
/// <remarks>
/// On first use the host prepares, under its own cgroup: <c>psr-host</c>, where it moves its own processes (cgroup v2
/// forbids processes in a cgroup that hands controllers to its children), the <c>memory</c> controller turned on, and
/// <c>psr-runs</c>. Each attempt then gets <c>psr-runs/&lt;id&gt;</c> with <c>memory.max</c>, which the SDK joins before
/// importing the script. Where the cgroup cannot be written (standard containers mount it read-only), <see cref="Create"/>
/// returns <c>null</c> and the limit falls back to <c>RLIMIT_AS</c> with the numeric libraries limited to
/// <see cref="PythonScriptRunnerOptions.ThreadsPerProcess"/> threads.
/// </remarks>
internal sealed class CgroupMemory
{
    private const string HostGroup = "psr-host";
    private const string RunsGroup = "psr-runs";

    private readonly ILogger _logger;
    private readonly IOptions<PythonScriptRunnerOptions> _options;
    private readonly string _root;
    private readonly string _procSelfCgroup;
    private readonly bool _linux;
    private readonly Lazy<string?> _runs;

    public CgroupMemory(ILogger<CgroupMemory> logger, IOptions<PythonScriptRunnerOptions> options)
        : this(logger, options, "/sys/fs/cgroup", "/proc/self/cgroup", OperatingSystem.IsLinux())
    {
    }

    /// <summary>Tests: a fake cgroup file system under <paramref name="root"/>.</summary>
    internal CgroupMemory(ILogger? logger, IOptions<PythonScriptRunnerOptions> options, string root, string procSelfCgroup, bool linux)
    {
        _logger = logger ?? NullLogger.Instance;
        _options = options;
        _root = root;
        _procSelfCgroup = procSelfCgroup;
        _linux = linux;
        _runs = new Lazy<string?>(Prepare, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The cgroup holding the attempt cgroups, or <c>null</c> when they are not available.</summary>
    public string? RunsPath => _runs.Value;

    /// <summary>Creates the cgroup of one attempt with <paramref name="limitBytes"/> of memory, or returns <c>null</c>.</summary>
    public CgroupLease? Create(long limitBytes)
    {
        if (_runs.Value is not { } runs)
        {
            return null;
        }

        var path = Path.Combine(runs, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(path);
            Write(Path.Combine(path, "memory.max"), limitBytes.ToString(CultureInfo.InvariantCulture));

            // Going over the limit ends in the OOM killer instead of swapping (absent without swap accounting).
            TryWrite(Path.Combine(path, "memory.swap.max"), "0");
            return new CgroupLease(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryRemove(path);
            return null;
        }
    }

    internal static void Write(string path, string value)
    {
        // One write(2) of the whole value: cgroup interface files take each write as a command.
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0);
        stream.Write(Encoding.ASCII.GetBytes(value));
    }

    internal static bool TryWrite(string path, string value)
    {
        try
        {
            Write(path, value);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void TryRemove(string path)
    {
        try
        {
            // rmdir: a cgroup without processes goes away with its interface files.
            Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still has processes, or already gone.
        }
    }

    private string? Prepare()
    {
        if (!_linux)
        {
            return null;
        }

        var threads = _options.Value.ThreadsPerProcess ?? 1;
        try
        {
            if (OwnCgroup() is not { } own)
            {
                Log.ProcessMemoryLimits(_logger, "no cgroup v2", threads);
                return null;
            }

            if (!Words(Path.Combine(own, "cgroup.controllers")).Contains("memory"))
            {
                Log.ProcessMemoryLimits(_logger, "the memory controller of the cgroup is not available", threads);
                return null;
            }

            if (!Words(Path.Combine(own, "cgroup.subtree_control")).Contains("memory"))
            {
                var host = Path.Combine(own, HostGroup);
                Directory.CreateDirectory(host);
                foreach (var pid in File.ReadAllLines(Path.Combine(own, "cgroup.procs")).Where(l => l.Length > 0))
                {
                    // A process may exit meanwhile; one that cannot move makes enabling the controller fail below.
                    TryWrite(Path.Combine(host, "cgroup.procs"), pid);
                }

                Write(Path.Combine(own, "cgroup.subtree_control"), "+memory");
            }

            var runs = Path.Combine(own, RunsGroup);
            Directory.CreateDirectory(runs);
            if (!Words(Path.Combine(runs, "cgroup.subtree_control")).Contains("memory"))
            {
                Write(Path.Combine(runs, "cgroup.subtree_control"), "+memory");
            }

            // Attempt cgroups left by a host that stopped abruptly (empty once their processes are gone).
            foreach (var leftover in Directory.EnumerateDirectories(runs))
            {
                TryRemove(leftover);
            }

            Log.CgroupMemoryLimits(_logger, runs);
            return runs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.ProcessMemoryLimits(_logger, ex.Message, threads);
            return null;
        }
    }

    /// <summary>The cgroup of this process: cgroup v2 lists one line <c>0::/path</c>.</summary>
    private string? OwnCgroup()
    {
        var line = File.Exists(_procSelfCgroup) ? File.ReadLines(_procSelfCgroup).FirstOrDefault(l => l.StartsWith("0::", StringComparison.Ordinal)) : null;
        if (line is null)
        {
            return null;
        }

        var own = Path.Combine(_root, line[3..].TrimStart('/'));
        return File.Exists(Path.Combine(own, "cgroup.controllers")) ? own : null;
    }

    private static HashSet<string> Words(string path) =>
        File.Exists(path) ? [.. File.ReadAllText(path).Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries)] : [];
}

/// <summary>The cgroup v2 of one attempt: out-of-memory check, kill and removal.</summary>
internal sealed class CgroupLease(string path) : IDisposable
{
    private static readonly TimeSpan EmptyWait = TimeSpan.FromSeconds(2);

    public string Path { get; } = path;

    /// <summary>Whether the kernel killed a process of the attempt for going over <c>memory.max</c>.</summary>
    public bool OutOfMemory()
    {
        try
        {
            return File.ReadLines(System.IO.Path.Combine(Path, "memory.events"))
                .Select(l => l.Split(' '))
                .Any(p => p.Length == 2 && p[0] == "oom_kill" && long.TryParse(p[1], CultureInfo.InvariantCulture, out var count) && count > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Kills every process left in the cgroup (<c>cgroup.kill</c>, Linux 5.14+).</summary>
    public void Kill() => CgroupMemory.TryWrite(System.IO.Path.Combine(Path, "cgroup.kill"), "1");

    public void Dispose()
    {
        Kill();
        var deadline = DateTime.UtcNow + EmptyWait;
        while (Populated() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        CgroupMemory.TryRemove(Path);
    }

    private bool Populated()
    {
        try
        {
            return File.ReadLines(System.IO.Path.Combine(Path, "cgroup.events")).Any(l => l == "populated 1");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
