using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using PythonScriptRunner.EntityFrameworkCore.Tests.Infrastructure;

namespace PythonScriptRunner.EntityFrameworkCore.Tests;

public sealed class LoggingTests(PostgresFixture postgres, PythonFixture python)
{
    /// <summary>
    /// The relay and the worker poll the database several times per second (D-58); with the executed commands at
    /// Information, the hosts logged ~4 lines per second of the same query (D-187).
    /// </summary>
    [Fact]
    public async Task ExecutedSqlCommands_AreLoggedAtDebug()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python, services: s => s
            .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug))
            .AddFakeLogging());

        (await host.Admin.GetStageAsync("inexistente", TestContext.Current.CancellationToken)).ShouldBeNull();

        var executed = host.Services.GetFakeLogCollector().GetSnapshot()
            .Where(r => r.Category == DbLoggerCategory.Database.Command.Name && r.Id.Id == RelationalEventId.CommandExecuted.Id)
            .ToList();
        executed.ShouldNotBeEmpty();
        executed.ShouldAllBe(r => r.Level == LogLevel.Debug);
    }
}
