using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace PythonScriptRunner.EntityFrameworkCore;

/// <summary>Npgsql settings shared by the runtime and the design-time context.</summary>
internal static class PsrDbContextOptions
{
    public static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable(PsrDbContext.MigrationsHistoryTable, PsrDbContext.Schema);
}
