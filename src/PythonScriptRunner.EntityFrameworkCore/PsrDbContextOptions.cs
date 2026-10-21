using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace PythonScriptRunner.EntityFrameworkCore;

/// <summary>Npgsql settings shared by the runtime and the design-time context.</summary>
internal static class PsrDbContextOptions
{
    public static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable(PsrDbContext.MigrationsHistoryTable, PsrDbContext.Schema);

    /// <summary>
    /// The outbox relay polls every 250 ms and the worker every few seconds (D-58), so each executed command is logged at
    /// <c>Debug</c> instead of <c>Information</c>: otherwise the host's log fills with the same queries (D-187). Failed
    /// commands are still logged as errors.
    /// </summary>
    public static void ConfigureWarnings(WarningsConfigurationBuilder warnings) =>
        warnings.Log((RelationalEventId.CommandExecuted, LogLevel.Debug));
}
