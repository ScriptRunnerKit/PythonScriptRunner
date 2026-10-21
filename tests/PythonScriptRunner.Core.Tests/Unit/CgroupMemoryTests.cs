using Microsoft.Extensions.Options;
using PythonScriptRunner.Core.Execution;

namespace PythonScriptRunner.Core.Tests.Unit;

/// <summary>Spec 001 (D-180): preparing cgroup v2 memory limits, over a fake cgroup file system.</summary>
public sealed class CgroupMemoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("psr-cgroup-").FullName;

    [Fact]
    public void Spec001_AC30_WithAWritableCgroup_MovesTheHostAside_AndEachAttemptGetsItsMemoryMax()
    {
        var own = Cgroup("servico", controllers: "cpu io memory pids", processes: "100\n");

        using var lease = Memory().Create(128L * 1024 * 1024);

        lease.ShouldNotBeNull();
        File.ReadAllText(Path.Combine(own, "psr-host", "cgroup.procs")).ShouldBe("100");
        File.ReadAllText(Path.Combine(own, "cgroup.subtree_control")).ShouldBe("+memory");
        File.ReadAllText(Path.Combine(own, "psr-runs", "cgroup.subtree_control")).ShouldBe("+memory");
        Path.GetDirectoryName(lease.Path).ShouldBe(Path.Combine(own, "psr-runs"));
        File.ReadAllText(Path.Combine(lease.Path, "memory.max")).ShouldBe("134217728");
        File.ReadAllText(Path.Combine(lease.Path, "memory.swap.max")).ShouldBe("0");
    }

    [Fact]
    public void Spec001_AC30_TheKernelOomKill_IsSeenInMemoryEvents()
    {
        Cgroup("servico", controllers: "memory", processes: "1\n");
        using var lease = Memory().Create(64L * 1024 * 1024)!;
        lease.OutOfMemory().ShouldBeFalse();

        File.WriteAllText(Path.Combine(lease.Path, "memory.events"), "low 0\nhigh 0\nmax 3\noom 1\noom_kill 1\n");

        lease.OutOfMemory().ShouldBeTrue();
    }

    [Fact]
    public void Spec001_AC29_WithoutTheMemoryController_OrOutsideLinux_ThereIsNoCgroup()
    {
        Cgroup("servico", controllers: "cpu pids", processes: "1\n");

        Memory().Create(64L * 1024 * 1024).ShouldBeNull();
        Memory(linux: false).Create(64L * 1024 * 1024).ShouldBeNull();
    }

    [Fact]
    public void Spec001_AC29_WithACgroupV1Host_ThereIsNoCgroup()
    {
        File.WriteAllText(Path.Combine(_root, "self-cgroup"), "12:memory:/servico\n1:name=systemd:/servico\n");

        Memory().Create(64L * 1024 * 1024).ShouldBeNull();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private CgroupMemory Memory(bool linux = true) =>
        new(null, Options.Create(new PythonScriptRunnerOptions()), Path.Combine(_root, "fs"), Path.Combine(_root, "self-cgroup"), linux);

    /// <summary>The cgroup of the host (as /proc/self/cgroup names it) with its interface files.</summary>
    private string Cgroup(string name, string controllers, string processes)
    {
        var own = Path.Combine(_root, "fs", name);
        Directory.CreateDirectory(own);
        File.WriteAllText(Path.Combine(own, "cgroup.controllers"), controllers + "\n");
        File.WriteAllText(Path.Combine(own, "cgroup.subtree_control"), "\n");
        File.WriteAllText(Path.Combine(own, "cgroup.procs"), processes);
        File.WriteAllText(Path.Combine(_root, "self-cgroup"), $"0::/{name}\n");
        return own;
    }
}
