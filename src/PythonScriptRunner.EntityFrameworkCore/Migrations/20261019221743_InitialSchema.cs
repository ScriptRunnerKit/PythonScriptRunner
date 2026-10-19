using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PythonScriptRunner.EntityFrameworkCore.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "psr");

            migrationBuilder.Sql(PartitioningSql.ArtifactContents);

            migrationBuilder.Sql(PartitioningSql.AuditEvents);

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                schema: "psr",
                columns: table => new
                {
                    scope = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => new { x.scope, x.key });
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "psr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    available_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    message_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    exchange = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    routing_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    headers = table.Column<string>(type: "jsonb", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pipelines",
                schema: "psr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    latest_published_version = table.Column<int>(type: "integer", nullable: true),
                    max_concurrent_runs = table.Column<int>(type: "integer", nullable: true),
                    artifact_retention_days = table.Column<int>(type: "integer", nullable: true),
                    run_retention_days = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pipelines", x => x.id);
                });

            migrationBuilder.Sql(PartitioningSql.Runs);

            migrationBuilder.CreateTable(
                name: "stages",
                schema: "psr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    latest_published_version = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pipeline_versions",
                schema: "psr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pipeline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Draft"),
                    input_fields = table.Column<string>(type: "jsonb", nullable: false),
                    outputs = table.Column<string>(type: "jsonb", nullable: false),
                    layout = table.Column<string>(type: "jsonb", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pipeline_versions", x => x.id);
                    table.CheckConstraint("ck_pipeline_versions_status", "status IN ('Draft', 'Published', 'Archived')");
                    table.ForeignKey(
                        name: "fk_pipeline_versions_pipelines_pipeline_id",
                        column: x => x.pipeline_id,
                        principalSchema: "psr",
                        principalTable: "pipelines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "schedules",
                schema: "psr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pipeline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    cron = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "America/Sao_Paulo"),
                    pinned_version = table.Column<int>(type: "integer", nullable: true),
                    inputs = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_fired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schedules", x => x.id);
                    table.ForeignKey(
                        name: "fk_schedules_pipelines_pipeline_id",
                        column: x => x.pipeline_id,
                        principalSchema: "psr",
                        principalTable: "pipelines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(PartitioningSql.StepRuns);

            migrationBuilder.CreateTable(
                name: "stage_versions",
                schema: "psr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Draft"),
                    script = table.Column<string>(type: "text", nullable: false),
                    script_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    python_version = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    requirements = table.Column<string>(type: "text", nullable: true),
                    locked_requirements = table.Column<string>(type: "text", nullable: true),
                    environment_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    input_fields = table.Column<string>(type: "jsonb", nullable: false),
                    output_fields = table.Column<string>(type: "jsonb", nullable: false),
                    required_secrets = table.Column<string[]>(type: "text[]", nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    memory_limit_mb = table.Column<int>(type: "integer", nullable: true),
                    retry_max_attempts = table.Column<int>(type: "integer", nullable: false),
                    retry_initial_delay_seconds = table.Column<int>(type: "integer", nullable: false),
                    retry_backoff_multiplier = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: false),
                    idempotent = table.Column<bool>(type: "boolean", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stage_versions", x => x.id);
                    table.CheckConstraint("ck_stage_versions_status", "status IN ('Draft', 'Published', 'Archived')");
                    table.ForeignKey(
                        name: "fk_stage_versions_stages_stage_id",
                        column: x => x.stage_id,
                        principalSchema: "psr",
                        principalTable: "stages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(PartitioningSql.Artifacts);

            migrationBuilder.Sql(PartitioningSql.StepAttempts);

            migrationBuilder.CreateTable(
                name: "pipeline_steps",
                schema: "psr",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pipeline_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    stage_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bindings = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pipeline_steps", x => x.id);
                    table.ForeignKey(
                        name: "fk_pipeline_steps_pipeline_versions_pipeline_version_id",
                        column: x => x.pipeline_version_id,
                        principalSchema: "psr",
                        principalTable: "pipeline_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_pipeline_steps_stage_versions_stage_version_id",
                        column: x => x.stage_version_id,
                        principalSchema: "psr",
                        principalTable: "stage_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(PartitioningSql.AttemptLogs);

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_content_expires_at",
                schema: "psr",
                table: "artifacts",
                column: "content_expires_at",
                filter: "content_deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_run_id_run_created_at",
                schema: "psr",
                table: "artifacts",
                columns: new[] { "run_id", "run_created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_artifacts_step_run_id_run_created_at",
                schema: "psr",
                table: "artifacts",
                columns: new[] { "step_run_id", "run_created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_occurred_at_id",
                schema: "psr",
                table: "audit_events",
                columns: new[] { "occurred_at", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_expires_at",
                schema: "psr",
                table: "idempotency_keys",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_available_at",
                schema: "psr",
                table: "outbox_messages",
                column: "available_at",
                filter: "published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pipeline_steps_stage_version_id",
                schema: "psr",
                table: "pipeline_steps",
                column: "stage_version_id");

            migrationBuilder.CreateIndex(
                name: "ux_pipeline_steps_pipeline_version_id_key",
                schema: "psr",
                table: "pipeline_steps",
                columns: new[] { "pipeline_version_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_pipeline_steps_pipeline_version_id_order",
                schema: "psr",
                table: "pipeline_steps",
                columns: new[] { "pipeline_version_id", "order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_pipeline_versions_draft",
                schema: "psr",
                table: "pipeline_versions",
                column: "pipeline_id",
                unique: true,
                filter: "status = 'Draft'");

            migrationBuilder.CreateIndex(
                name: "ux_pipeline_versions_pipeline_id_version",
                schema: "psr",
                table: "pipeline_versions",
                columns: new[] { "pipeline_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_pipelines_key",
                schema: "psr",
                table: "pipelines",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_runs_created_at_id",
                schema: "psr",
                table: "runs",
                columns: new[] { "created_at", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_runs_expires_at",
                schema: "psr",
                table: "runs",
                column: "expires_at",
                filter: "expires_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_runs_pipeline_id_created_at_id",
                schema: "psr",
                table: "runs",
                columns: new[] { "pipeline_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_runs_stage_key_created_at_id",
                schema: "psr",
                table: "runs",
                columns: new[] { "stage_key", "created_at", "id" },
                descending: new[] { false, true, true },
                filter: "stage_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_runs_status_created_at",
                schema: "psr",
                table: "runs",
                columns: new[] { "status", "created_at" },
                filter: "status IN ('Pending', 'Queued', 'Running', 'Cancelling')");

            migrationBuilder.CreateIndex(
                name: "ix_schedules_next_run_at",
                schema: "psr",
                table: "schedules",
                column: "next_run_at",
                filter: "is_enabled");

            migrationBuilder.CreateIndex(
                name: "ix_schedules_pipeline_id",
                schema: "psr",
                table: "schedules",
                column: "pipeline_id");

            migrationBuilder.CreateIndex(
                name: "ux_stage_versions_draft",
                schema: "psr",
                table: "stage_versions",
                column: "stage_id",
                unique: true,
                filter: "status = 'Draft'");

            migrationBuilder.CreateIndex(
                name: "ux_stage_versions_stage_id_version",
                schema: "psr",
                table: "stage_versions",
                columns: new[] { "stage_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_stages_key",
                schema: "psr",
                table: "stages",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_step_attempts_lease_expires_at",
                schema: "psr",
                table: "step_attempts",
                column: "lease_expires_at",
                filter: "status = 'Running'");

            migrationBuilder.CreateIndex(
                name: "ix_step_attempts_step_run_id_run_created_at",
                schema: "psr",
                table: "step_attempts",
                columns: new[] { "step_run_id", "run_created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_step_runs_run_id_run_created_at_order",
                schema: "psr",
                table: "step_runs",
                columns: new[] { "run_id", "run_created_at", "order" },
                unique: true);

            migrationBuilder.Sql(PartitioningSql.EnsurePartitionsFunction);
            migrationBuilder.Sql(PartitioningSql.DropExpiredPartitionsFunction);
            migrationBuilder.Sql(PartitioningSql.CreateInitialPartitions);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PartitioningSql.DropFunctions);

            migrationBuilder.DropTable(
                name: "artifact_contents",
                schema: "psr");

            migrationBuilder.DropTable(
                name: "artifacts",
                schema: "psr");

            migrationBuilder.DropTable(
                name: "attempt_logs",
                schema: "psr");

            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "psr");

            migrationBuilder.DropTable(
                name: "idempotency_keys",
                schema: "psr");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "psr");

            migrationBuilder.DropTable(
                name: "pipeline_steps",
                schema: "psr");

            migrationBuilder.DropTable(
                name: "schedules",
                schema: "psr");

            migrationBuilder.DropTable(
                name: "step_attempts",
                schema: "psr");

            migrationBuilder.DropTable(
                name: "pipeline_versions",
                schema: "psr");

            migrationBuilder.DropTable(
                name: "stage_versions",
                schema: "psr");

            migrationBuilder.DropTable(
                name: "step_runs",
                schema: "psr");

            migrationBuilder.DropTable(
                name: "pipelines",
                schema: "psr");

            migrationBuilder.DropTable(
                name: "stages",
                schema: "psr");

            migrationBuilder.DropTable(
                name: "runs",
                schema: "psr");
        }
    }
}
