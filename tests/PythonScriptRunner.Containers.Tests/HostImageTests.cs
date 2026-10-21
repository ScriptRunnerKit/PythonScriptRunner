using System.Net;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;
using PythonScriptRunner.Containers.Tests.Infrastructure;
using PythonScriptRunner.EntityFrameworkCore;

namespace PythonScriptRunner.Containers.Tests;

/// <summary>Spec 009: the worker and dashboard images as they run in production.</summary>
public sealed class HostImageTests(HostStack stack) : ContainerTest
{
    private const long MaxWorkerImageBytes = 400L * 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spec009_AC07_BothImages_RunAsNonRootOnAReadOnlyFileSystem_AndAnswerTheHealthChecks()
    {
        var database = await stack.CreateDatabaseAsync("ac07");
        await using var worker = stack.Host(stack.WorkerImage, database);
        await using var dashboard = stack.Host(stack.DashboardImage, database, HostStack.DashboardUsers());

        await worker.StartAsync(Ct);
        await dashboard.StartAsync(Ct);

        foreach (var host in new[] { worker, dashboard })
        {
            using var http = Client(host);
            (await http.GetAsync("/health/live", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await http.GetAsync("/health/ready", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await host.ExecAsync(["id", "-u"], Ct)).Stdout.Trim().ShouldBe("10001");
            (await host.ExecAsync(["sh", "-c", "touch /app/escrita"], Ct)).ExitCode.ShouldNotBe(0, "the root file system is read-only");

            // The psrunner wheel of the same commit (D-169) is what the host installs in each environment.
            var environment = (await host.ExecAsync(["sh", "-c", "tr '\\0' '\\n' < /proc/1/environ"], Ct)).Stdout;
            environment.ShouldContain("PythonScriptRunner__PsrunnerRequirement=/opt/psr/psrunner-");
        }

        using var panel = Client(dashboard);
        (await panel.GetStringAsync("/psr/", Ct)).ShouldContain("id=\"app\"", customMessage: "the image embeds the built panel");
    }

    [Fact]
    public async Task Spec009_AC08_TwoWorkersWithAutoMigrateStartingTogether_ApplyTheMigrationsOnce()
    {
        var database = await stack.CreateDatabaseAsync("ac08");
        await using var first = stack.Host(stack.WorkerImage, database);
        await using var second = stack.Host(stack.WorkerImage, database);

        await Task.WhenAll(first.StartAsync(Ct), second.StartAsync(Ct));

        var migrations = typeof(PsrDbContext).Assembly.GetTypes().Count(t => t.GetCustomAttribute<MigrationAttribute>() is not null);
        migrations.ShouldBeGreaterThan(0);
        var applied = await stack.ScalarAsync(database, $"SELECT count(*) FROM {PsrDbContext.Schema}.{PsrDbContext.MigrationsHistoryTable}");
        applied.ShouldBe(migrations.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var worker in new[] { first, second })
        {
            var (stdout, stderr) = await worker.GetLogsAsync(ct: Ct);
            (stdout + stderr).ShouldNotContain("Hosting failed to start");
        }
    }

    [Fact]
    public async Task Spec009_WorkerImage_IsAtMost400MBWithoutPythonInterpreters()
    {
        var (code, output) = await HostImages.DockerAsync(["image", "inspect", "--format", "{{.Size}}", stack.WorkerImage], Ct);

        code.ShouldBe(0, output);
        long.Parse(output.Trim(), System.Globalization.CultureInfo.InvariantCulture).ShouldBeLessThanOrEqualTo(MaxWorkerImageBytes);
    }

    private static HttpClient Client(IContainer host) =>
        new() { BaseAddress = new Uri($"http://{host.Hostname}:{host.GetMappedPublicPort(HostStack.HttpPort)}") };
}
