using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PythonScriptRunner.EntityFrameworkCore.Migrations
{
    /// <inheritdoc />
    public partial class AsyncRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "trigger_details",
                schema: "psr",
                table: "runs",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "trigger_details",
                schema: "psr",
                table: "runs");
        }
    }
}
