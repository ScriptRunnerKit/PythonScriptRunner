using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PythonScriptRunner.EntityFrameworkCore.Migrations;

/// <summary>Lets <c>dotnet ef migrations add</c> build the model without a host (no database connection is opened).</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<PsrDbContext>
{
    public PsrDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PsrDbContext>()
            .UseNpgsql("Host=localhost;Database=psr_design", PsrDbContextOptions.ConfigureNpgsql)
            .Options);
}
