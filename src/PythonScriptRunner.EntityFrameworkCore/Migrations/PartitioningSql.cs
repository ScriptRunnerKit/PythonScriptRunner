namespace PythonScriptRunner.EntityFrameworkCore.Migrations;

/// <summary>
/// SQL that EF Core cannot model: history tables partitioned by range (weekly with the run, daily for file contents,
/// monthly for audit) and the functions that create and drop partitions (called by the migration and the maintenance job).
/// </summary>
internal static class PartitioningSql
{
    /// <summary><c>psr.runs</c>, partitioned by range.</summary>
    public const string Runs = """
        CREATE TABLE psr.runs (
            id uuid NOT NULL,
            created_at timestamp with time zone NOT NULL,
            kind character varying(16) NOT NULL,
            mode character varying(8) NOT NULL,
            pipeline_id uuid,
            pipeline_version_id uuid,
            pipeline_key character varying(64),
            pipeline_version integer,
            stage_version_id uuid,
            stage_key character varying(64),
            stage_version integer,
            trigger character varying(16) NOT NULL,
            triggered_by character varying(200) NOT NULL,
            schedule_id uuid,
            status character varying(16) NOT NULL,
            inputs jsonb NOT NULL,
            outputs jsonb,
            error jsonb,
            idempotency_key character varying(200),
            correlation_id character varying(200),
            trace_parent character varying(55),
            queued_at timestamp with time zone,
            started_at timestamp with time zone,
            finished_at timestamp with time zone,
            cancel_requested_at timestamp with time zone,
            cancel_requested_by character varying(200),
            expires_at timestamp with time zone,
            CONSTRAINT pk_runs PRIMARY KEY (id, created_at),
            CONSTRAINT ck_runs_kind CHECK (kind IN ('Pipeline', 'PipelineTest', 'Stage', 'StageTest')),
            CONSTRAINT ck_runs_mode CHECK (mode IN ('Sync', 'Async')),
            CONSTRAINT ck_runs_status CHECK (status IN ('Pending', 'Queued', 'Running', 'Succeeded', 'Failed', 'Cancelling', 'Cancelled')),
            CONSTRAINT ck_runs_trigger CHECK (trigger IN ('Library', 'Dashboard', 'Schedule', 'QueueCommand'))
        ) PARTITION BY RANGE (created_at);
        """;

    /// <summary><c>psr.step_runs</c>, partitioned by range.</summary>
    public const string StepRuns = """
        CREATE TABLE psr.step_runs (
            id uuid NOT NULL,
            run_created_at timestamp with time zone NOT NULL,
            run_id uuid NOT NULL,
            pipeline_step_id uuid,
            step_key character varying(64) NOT NULL,
            "order" integer NOT NULL,
            stage_version_id uuid,
            stage_key character varying(64) NOT NULL,
            stage_version integer NOT NULL,
            status character varying(16) NOT NULL,
            current_attempt integer NOT NULL,
            inputs jsonb,
            outputs jsonb,
            error jsonb,
            started_at timestamp with time zone,
            finished_at timestamp with time zone,
            CONSTRAINT pk_step_runs PRIMARY KEY (id, run_created_at),
            CONSTRAINT ck_step_runs_status CHECK (status IN ('Pending', 'Queued', 'Running', 'Succeeded', 'Failed', 'TimedOut', 'Cancelled', 'Skipped')),
            CONSTRAINT fk_step_runs_runs_run_id_run_created_at FOREIGN KEY (run_id, run_created_at) REFERENCES psr.runs (id, created_at) ON DELETE CASCADE
        ) PARTITION BY RANGE (run_created_at);
        """;

    /// <summary><c>psr.step_attempts</c>, partitioned by range.</summary>
    public const string StepAttempts = """
        CREATE TABLE psr.step_attempts (
            id uuid NOT NULL,
            run_created_at timestamp with time zone NOT NULL,
            step_run_id uuid NOT NULL,
            run_id uuid NOT NULL,
            attempt integer NOT NULL,
            cause character varying(16) NOT NULL,
            triggered_by character varying(200) NOT NULL,
            status character varying(16) NOT NULL,
            worker_id character varying(200),
            lease_expires_at timestamp with time zone,
            environment_hash character(64),
            exit_code integer,
            error_kind character varying(40),
            error jsonb,
            started_at timestamp with time zone NOT NULL,
            finished_at timestamp with time zone,
            duration_ms integer,
            overhead_ms integer,
            CONSTRAINT pk_step_attempts PRIMARY KEY (id, run_created_at),
            CONSTRAINT ck_step_attempts_cause CHECK (cause IN ('Initial', 'AutoRetry', 'Reprocess', 'LeaseRecovery')),
            CONSTRAINT ck_step_attempts_status CHECK (status IN ('Running', 'Succeeded', 'Failed', 'TimedOut', 'Cancelled', 'WorkerLost')),
            CONSTRAINT fk_step_attempts_step_runs_step_run_id_run_created_at FOREIGN KEY (step_run_id, run_created_at) REFERENCES psr.step_runs (id, run_created_at) ON DELETE CASCADE
        ) PARTITION BY RANGE (run_created_at);
        """;

    /// <summary><c>psr.attempt_logs</c>, partitioned by range.</summary>
    public const string AttemptLogs = """
        CREATE TABLE psr.attempt_logs (
            attempt_id uuid NOT NULL,
            run_created_at timestamp with time zone NOT NULL,
            stream character varying(8) NOT NULL,
            content text NOT NULL,
            truncated boolean NOT NULL,
            original_bytes bigint NOT NULL,
            CONSTRAINT pk_attempt_logs PRIMARY KEY (attempt_id, run_created_at, stream),
            CONSTRAINT ck_attempt_logs_stream CHECK (stream IN ('stdout', 'stderr')),
            CONSTRAINT fk_attempt_logs_step_attempts_attempt_id_run_created_at FOREIGN KEY (attempt_id, run_created_at) REFERENCES psr.step_attempts (id, run_created_at) ON DELETE CASCADE
        ) PARTITION BY RANGE (run_created_at);
        """;

    /// <summary><c>psr.artifacts</c>, partitioned by range.</summary>
    public const string Artifacts = """
        CREATE TABLE psr.artifacts (
            id uuid NOT NULL,
            run_created_at timestamp with time zone NOT NULL,
            run_id uuid NOT NULL,
            step_run_id uuid,
            direction character varying(8) NOT NULL,
            field_name character varying(64) NOT NULL,
            list_index integer,
            file_name character varying(255) NOT NULL,
            content_type character varying(255) NOT NULL,
            size_bytes bigint NOT NULL,
            sha256 character(64) NOT NULL,
            content_created_at timestamp with time zone NOT NULL,
            content_expires_at timestamp with time zone NOT NULL,
            content_deleted_at timestamp with time zone,
            CONSTRAINT pk_artifacts PRIMARY KEY (id, run_created_at),
            CONSTRAINT ck_artifacts_direction CHECK (direction IN ('Input', 'Output')),
            CONSTRAINT fk_artifacts_runs_run_id_run_created_at FOREIGN KEY (run_id, run_created_at) REFERENCES psr.runs (id, created_at) ON DELETE CASCADE,
            CONSTRAINT fk_artifacts_step_runs_step_run_id_run_created_at FOREIGN KEY (step_run_id, run_created_at) REFERENCES psr.step_runs (id, run_created_at) ON DELETE CASCADE
        ) PARTITION BY RANGE (run_created_at);
        """;

    /// <summary><c>psr.artifact_contents</c>, partitioned by range.</summary>
    public const string ArtifactContents = """
        CREATE TABLE psr.artifact_contents (
            artifact_id uuid NOT NULL,
            created_at timestamp with time zone NOT NULL,
            content bytea NOT NULL,
            CONSTRAINT pk_artifact_contents PRIMARY KEY (artifact_id, created_at)
        ) PARTITION BY RANGE (created_at);
        """;

    /// <summary><c>psr.audit_events</c>, partitioned by range.</summary>
    public const string AuditEvents = """
        CREATE TABLE psr.audit_events (
            id uuid NOT NULL,
            occurred_at timestamp with time zone NOT NULL,
            actor character varying(200) NOT NULL,
            action character varying(60) NOT NULL,
            entity_type character varying(30) NOT NULL,
            entity_id uuid,
            entity_key character varying(100),
            details jsonb,
            trace_id character varying(32),
            CONSTRAINT pk_audit_events PRIMARY KEY (id, occurred_at)
        ) PARTITION BY RANGE (occurred_at);
        """;

    /// <summary>Creates the partitions of the current period and of the periods ahead (idempotent).</summary>
    public const string EnsurePartitionsFunction = """
        CREATE OR REPLACE FUNCTION psr.ensure_partitions(
            p_now timestamptz, p_weeks_ahead integer DEFAULT 4, p_days_ahead integer DEFAULT 7, p_months_ahead integer DEFAULT 3)
        RETURNS integer
        LANGUAGE plpgsql
        AS $fn$
        DECLARE
            v_created integer := 0;
            v_table text;
            v_start timestamptz;
            v_end timestamptz;
            v_name text;
            v_week timestamp := date_trunc('week', p_now AT TIME ZONE 'UTC');
            v_day timestamp := date_trunc('day', p_now AT TIME ZONE 'UTC');
            v_month timestamp := date_trunc('month', p_now AT TIME ZONE 'UTC');
            i integer;
        BEGIN
            FOREACH v_table IN ARRAY ARRAY['runs', 'step_runs', 'step_attempts', 'attempt_logs', 'artifacts'] LOOP
                FOR i IN 0..p_weeks_ahead LOOP
                    v_start := (v_week + make_interval(weeks => i)) AT TIME ZONE 'UTC';
                    v_end := v_start + interval '7 days';
                    v_name := v_table || '_p' || to_char(v_start AT TIME ZONE 'UTC', 'YYYYMMDD');
                    IF to_regclass(format('psr.%I', v_name)) IS NULL THEN
                        EXECUTE format('CREATE TABLE psr.%I PARTITION OF psr.%I FOR VALUES FROM (%L) TO (%L)', v_name, v_table, v_start, v_end);
                        v_created := v_created + 1;
                    END IF;
                END LOOP;
            END LOOP;

            FOR i IN 0..p_days_ahead LOOP
                v_start := (v_day + make_interval(days => i)) AT TIME ZONE 'UTC';
                v_end := v_start + interval '1 day';
                v_name := 'artifact_contents_p' || to_char(v_start AT TIME ZONE 'UTC', 'YYYYMMDD');
                IF to_regclass(format('psr.%I', v_name)) IS NULL THEN
                    EXECUTE format('CREATE TABLE psr.%I PARTITION OF psr.artifact_contents FOR VALUES FROM (%L) TO (%L)', v_name, v_start, v_end);
                    v_created := v_created + 1;
                END IF;
            END LOOP;

            FOR i IN 0..p_months_ahead LOOP
                v_start := (v_month + make_interval(months => i)) AT TIME ZONE 'UTC';
                v_end := (v_month + make_interval(months => i + 1)) AT TIME ZONE 'UTC';
                v_name := 'audit_events_p' || to_char(v_start AT TIME ZONE 'UTC', 'YYYYMM');
                IF to_regclass(format('psr.%I', v_name)) IS NULL THEN
                    EXECUTE format('CREATE TABLE psr.%I PARTITION OF psr.audit_events FOR VALUES FROM (%L) TO (%L)', v_name, v_start, v_end);
                    v_created := v_created + 1;
                END IF;
            END LOOP;

            RETURN v_created;
        END
        $fn$;
        """;

    /// <summary>Detaches and drops the partitions older than the global retention.</summary>
    public const string DropExpiredPartitionsFunction = """
        CREATE OR REPLACE FUNCTION psr.drop_expired_partitions(
            p_now timestamptz, p_runs interval, p_contents interval, p_audit interval)
        RETURNS SETOF text
        LANGUAGE plpgsql
        AS $fn$
        DECLARE
            v_row record;
            v_upper timestamptz;
            v_retention interval;
        BEGIN
            -- Children first: the foreign keys of the history tables point to runs and step_runs.
            FOR v_row IN
                SELECT child.relname::text AS name, parent.relname::text AS parent, pg_get_expr(child.relpartbound, child.oid) AS bound
                FROM pg_inherits inh
                JOIN pg_class child ON child.oid = inh.inhrelid
                JOIN pg_class parent ON parent.oid = inh.inhparent
                JOIN pg_namespace ns ON ns.oid = parent.relnamespace
                WHERE ns.nspname = 'psr'
                  AND parent.relname IN ('attempt_logs', 'step_attempts', 'artifacts', 'step_runs', 'runs', 'artifact_contents', 'audit_events')
                ORDER BY array_position(
                    ARRAY['attempt_logs', 'step_attempts', 'artifacts', 'step_runs', 'runs', 'artifact_contents', 'audit_events'], parent.relname::text),
                    child.relname
            LOOP
                v_upper := substring(v_row.bound FROM 'TO \(''([^'']+)''\)')::timestamptz;
                v_retention := CASE v_row.parent WHEN 'artifact_contents' THEN p_contents WHEN 'audit_events' THEN p_audit ELSE p_runs END;
                IF v_upper <= p_now - v_retention THEN
                    EXECUTE format('ALTER TABLE psr.%I DETACH PARTITION psr.%I', v_row.parent, v_row.name);
                    EXECUTE format('DROP TABLE psr.%I', v_row.name);
                    RETURN NEXT v_row.name;
                END IF;
            END LOOP;
        END
        $fn$;
        """;

    /// <summary>Creates the partitions of the current period and the ones ahead.</summary>
    public const string CreateInitialPartitions = "DO $do$ BEGIN PERFORM psr.ensure_partitions(now()); END $do$;";

    /// <summary>Removes the partition functions.</summary>
    public const string DropFunctions = """
        DROP FUNCTION IF EXISTS psr.drop_expired_partitions(timestamptz, interval, interval, interval);
        DROP FUNCTION IF EXISTS psr.ensure_partitions(timestamptz, integer, integer, integer);
        """;
}
