// Gerado por scripts/generate-api.mjs a partir do snapshot do OpenAPI. Não edite à mão: rode `npm run api`.

export interface paths {
    "/auth": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetAuth"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/auth/login": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["Login"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/auth/logout": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["Logout"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/me": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetMe"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/import": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["Import"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/overview": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetOverview"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/artifacts/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["DownloadArtifact"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/audit": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["ListAudit"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/admin/secrets": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["ListSecrets"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/admin/settings": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetSettings"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/meta/field-types": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetFieldTypes"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/stages": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["ListStages"];
        put?: never;
        post: operations["CreateStage"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/stages/{key}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetStage"];
        put?: never;
        post?: never;
        delete: operations["DeleteStage"];
        options?: never;
        head?: never;
        patch: operations["UpdateStage"];
        trace?: never;
    };
    "/stages/{key}/versions/{version}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetStageVersion"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/stages/{key}/draft": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetStageDraft"];
        put: operations["SaveStageDraft"];
        post: operations["CreateStageDraft"];
        delete: operations["DiscardStageDraft"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/stages/{key}/draft/validate": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ValidateStageDraft"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/stages/{key}/draft/test": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["TestStageDraft"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/stages/{key}/draft/publish": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PublishStage"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/stages/{key}/versions/{version}/archive": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ArchiveStageVersion"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/stages/{key}/versions/{version}/export": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["ExportStageVersion"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/stages/{key}/runs": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RunStage"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/pipelines": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["ListPipelines"];
        put?: never;
        post: operations["CreatePipeline"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/pipelines/validate": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ValidatePipeline"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/pipelines/{key}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetPipeline"];
        put?: never;
        post?: never;
        delete: operations["DeletePipeline"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/pipelines/{key}/versions/{version}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetPipelineVersion"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/pipelines/{key}/draft": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetPipelineDraft"];
        put: operations["SavePipelineDraft"];
        post: operations["CreatePipelineDraft"];
        delete: operations["DiscardPipelineDraft"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/pipelines/{key}/draft/test": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["TestPipelineDraft"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/pipelines/{key}/draft/publish": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PublishPipeline"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/pipelines/{key}/versions/{version}/archive": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ArchivePipelineVersion"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/pipelines/{key}/settings": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch: operations["UpdatePipelineSettings"];
        trace?: never;
    };
    "/pipelines/{key}/retention": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch: operations["UpdatePipelineRetention"];
        trace?: never;
    };
    "/pipelines/{key}/versions/{version}/export": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["ExportPipelineVersion"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/pipelines/{key}/runs": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RunPipeline"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/pipelines/{key}/schedules": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CreateSchedule"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/runs": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["ListRuns"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/runs/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetRun"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/runs/{id}/attempts/{attemptId}/logs/{stream}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetAttemptLog"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/runs/{id}/cancel": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CancelRun"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/runs/{id}/reprocess": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ReprocessRun"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/schedules": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["ListSchedules"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/schedules/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put: operations["UpdateSchedule"];
        post?: never;
        delete: operations["DeleteSchedule"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/schedules/{id}/enable": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["EnableSchedule"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/schedules/{id}/disable": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["DisableSchedule"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/schedules/preview": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["PreviewSchedule"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
}
export type webhooks = Record<string, never>;
export interface components {
    schemas: {
        /** @description One page of a catalog list. */
        ApiPageOfPipelineSummary: {
            /** @description Items of the page. */
            items: components["schemas"]["PipelineSummary"][];
            /**
             * Format: int32
             * @description 1-based page number.
             */
            page: number;
            /**
             * Format: int32
             * @description Items per page.
             */
            pageSize: number;
            /**
             * Format: int32
             * @description Number of items in every page.
             */
            total: number;
        };
        /** @description One page of a catalog list. */
        ApiPageOfScheduleDetail: {
            /** @description Items of the page. */
            items: components["schemas"]["ScheduleDetail"][];
            /**
             * Format: int32
             * @description 1-based page number.
             */
            page: number;
            /**
             * Format: int32
             * @description Items per page.
             */
            pageSize: number;
            /**
             * Format: int32
             * @description Number of items in every page.
             */
            total: number;
        };
        /** @description One page of a catalog list. */
        ApiPageOfStageSummary: {
            /** @description Items of the page. */
            items: components["schemas"]["StageSummary"][];
            /**
             * Format: int32
             * @description 1-based page number.
             */
            page: number;
            /**
             * Format: int32
             * @description Items per page.
             */
            pageSize: number;
            /**
             * Format: int32
             * @description Number of items in every page.
             */
            total: number;
        };
        /**
         * @description Whether a stored file was given to a run or produced by it.
         * @enum {unknown}
         */
        ArtifactDirection: "Input" | "Output";
        /**
         * Format: uuid
         * @description Identifier of a stored file (time-ordered UUID v7).
         */
        ArtifactId: string;
        /** @description Metadata of a stored file. */
        ArtifactInfo: {
            /** @description File identifier. */
            id: components["schemas"]["ArtifactId"];
            /** @description Run that owns the file. */
            runId: components["schemas"]["RunId"];
            /** @description Step that produced the file (`null` for run inputs). */
            stepKey?: null | string;
            /** @description Input or output. */
            direction: components["schemas"]["ArtifactDirection"];
            /** @description Field that carries the file. */
            fieldName: string;
            /**
             * Format: int32
             * @description Position in a `list&lt;file&gt;`.
             */
            listIndex?: number | null;
            /** @description File name. */
            fileName: string;
            /** @description MIME type. */
            contentType: string;
            /**
             * Format: int64
             * @description Size in bytes.
             */
            sizeBytes: number;
            /** @description SHA-256 as lowercase hex. */
            sha256: string;
            /**
             * Format: date-time
             * @description When the content is deleted.
             */
            contentExpiresAt: string;
            /**
             * Format: date-time
             * @description When the content was deleted (downloads then fail with `ARTIFACT_EXPIRED`).
             */
            contentDeletedAt?: null | string;
        };
        /**
         * @description Why an attempt was started.
         * @enum {unknown}
         */
        AttemptCause: "Initial" | "AutoRetry" | "Reprocess" | "LeaseRecovery";
        /**
         * Format: uuid
         * @description Identifier of one attempt of a step (time-ordered UUID v7).
         */
        AttemptId: string;
        /** @description A persisted attempt (one Python process). */
        AttemptSnapshot: {
            /** @description Attempt identifier. */
            id: components["schemas"]["AttemptId"];
            /**
             * Format: int32
             * @description 1-based number within the step.
             */
            number: number;
            /** @description Why it started. */
            cause: components["schemas"]["AttemptCause"];
            /** @description Who started it (user for reprocessing, `system:*` otherwise). */
            triggeredBy: string;
            /** @description Status. */
            status: components["schemas"]["AttemptStatus"];
            /** @description Worker that executed it (asynchronous runs). */
            workerId?: null | string;
            /** @description Python environment used. */
            environmentHash?: null | string;
            /**
             * Format: int32
             * @description Exit code of the process.
             */
            exitCode?: number | null;
            error?: null | components["schemas"]["RunError"];
            /**
             * Format: date-time
             * @description Start time.
             */
            startedAt: string;
            /**
             * Format: date-time
             * @description End time.
             */
            finishedAt?: null | string;
            /** @description Total duration. */
            duration?: null | string;
            /** @description Time spent by the runner outside the Python process. */
            overhead?: null | string;
            /** @description Logs captured (content is read with Task&lt;AttemptLog?&gt; IRunManager.GetAttemptLogAsync(RunId runId, AttemptId attemptId, LogStream stream, CancellationToken cancellationToken = default(CancellationToken))). */
            logs?: components["schemas"]["LogStream"][];
        };
        /**
         * @description Final status of one attempt.
         * @enum {unknown}
         */
        AttemptStatus: "Running" | "Succeeded" | "Failed" | "TimedOut" | "Cancelled" | "WorkerLost";
        /** @description A stored audit event. */
        AuditEvent: {
            /**
             * Format: uuid
             * @description Event identifier.
             */
            id: string;
            /**
             * Format: date-time
             * @description When it happened.
             */
            occurredAt: string;
            /** @description Subject of the user. */
            actor: string;
            /** @description What was done. */
            action: string;
            /** @description Entity type. */
            entityType: string;
            /**
             * Format: uuid
             * @description Entity identifier.
             */
            entityId: null | string;
            /** @description Readable reference. */
            entityKey: null | string;
            details: null | components["schemas"]["JsonObject"];
            /** @description OpenTelemetry trace. */
            traceId: null | string;
        };
        /** @description How the panel signs in (`GET /auth`); never lists users. */
        AuthInfo: {
            /** @description Access mode. */
            mode: components["schemas"]["DashboardAccessMode"];
            /** @description Host login page (host authentication). */
            loginUrl: null | string;
            /** @description Host logout page (host authentication). */
            logoutUrl: null | string;
            /** @description Whether local requests enter without login. */
            localRequests: boolean;
        };
        /** @description Creation and last change of a row. */
        Authorship: {
            /**
             * Format: date-time
             * @description Creation time.
             */
            createdAt: string;
            /** @description Creator subject. */
            createdBy: string;
            /**
             * Format: date-time
             * @description Last change.
             */
            updatedAt: string;
            /** @description Subject of the last change. */
            updatedBy: string;
        };
        /** @description Origin of a bound value. */
        BindingSource: {
            /** @enum {string} */
            kind: "pipeline";
            field: string;
        } | {
            /** @enum {string} */
            kind: "step";
            step: string;
            field: string;
        } | {
            /** @enum {string} */
            kind: "constant";
            value?: unknown;
        };
        /** @description Changes to the name or description; `null` keeps the current value. */
        CatalogItemChanges: {
            /** @description New name. */
            name?: null | string;
            /** @description New description. */
            description?: null | string;
        };
        /**
         * @description How the dashboard identifies its users (ADR-0021).
         * @enum {unknown}
         */
        DashboardAccessMode: "FixedLogin" | "Filter" | "Host" | "Localhost";
        /** @enum {unknown} */
        DashboardRole: "Reader" | "Executor" | "Author" | "Admin";
        /**
         * @description Classification of a failure.
         * @enum {unknown}
         */
        ErrorKind: "InputValidation" | "OutputContractViolation" | "ScriptError" | "Timeout" | "MemoryLimitExceeded" | "Cancelled" | "WorkerLost" | "EnvironmentSetupFailed" | "ArtifactLimitExceeded" | "SecretNotFound" | "ProcessCrashed" | "Expired";
        /**
         * @description How a run is executed.
         * @enum {unknown}
         */
        ExecutionMode: "Sync" | "Async";
        /** @description One field type. */
        FieldTypeInfo: {
            /** @description Wire name (`number`, `list`...). */
            name: string;
            /** @description Whether it is a scalar. */
            scalar: boolean;
            /** @description Widget of the input form. */
            inputWidget: string;
            /** @description How outputs are shown. */
            outputWidget: string;
            /** @description Whether a default value can be declared. */
            allowsDefault: boolean;
        };
        /** @description Field types for the editor (`GET /meta/field-types`). */
        FieldTypesResponse: {
            /** @description The 12 types and their widgets. */
            types: components["schemas"]["FieldTypeInfo"][];
            /** @description Which source types can feed which target types (representative types; see Rules). */
            compatibility: {
                [key: string]: string[];
            };
            /** @description Rules that depend on the declaration (enum values, table columns, file content types, list items). */
            rules: string[];
        };
        /** @description A draft created by an import. */
        ImportedItem: {
            /** @description `Stage` or `Pipeline`. */
            kind: string;
            /** @description Key. */
            key: string;
            /**
             * Format: int32
             * @description Version of the created draft.
             */
            version: number;
            /** @description Whether the key did not exist before. */
            newKey: boolean;
        };
        /** @description Result of an import. */
        ImportReport: {
            /** @description Drafts created. */
            items: components["schemas"]["ImportedItem"][];
        };
        /** @description Where one input of a step comes from. */
        InputBinding: {
            /** @description Input name of the stage. */
            input: string;
            /** @description Origin of the value. */
            source: components["schemas"]["BindingSource"];
        };
        JsonNode: unknown;
        JsonObject: Record<string, unknown>;
        /** @description One page of a keyset-paginated list (newest first). */
        KeysetPageOfAuditEvent: {
            /** @description Items of the page. */
            items: components["schemas"]["AuditEvent"][];
            /** @description Opaque cursor of the next page; `null` on the last page. */
            nextCursor: null | string;
        };
        /** @description One page of a keyset-paginated list (newest first). */
        KeysetPageOfRunSummary: {
            /** @description Items of the page. */
            items: components["schemas"]["RunSummary"][];
            /** @description Opaque cursor of the next page; `null` on the last page. */
            nextCursor: null | string;
        };
        /** @description Limits of runs and uploads. */
        LimitSettings: {
            /**
             * Format: int64
             * @description Largest input or output file.
             */
            maxFileBytes: number;
            /**
             * Format: int64
             * @description Largest total of the files of a run.
             */
            maxRunFilesBytes: number;
            /**
             * Format: int32
             * @description Log tail kept per stream.
             */
            maxLogBytes: number;
            /** @description Default timeout of an attempt. */
            defaultTimeout: string;
            /**
             * Format: int32
             * @description Default memory limit.
             */
            defaultMemoryLimitMb: number;
            /**
             * Format: int32
             * @description Python processes per instance.
             */
            maxConcurrentProcesses: number;
            /** @description Default Python version. */
            defaultPythonVersion: string;
        };
        /** @description Fixed login credentials. */
        LoginRequest: {
            /** @description User name. */
            name: null | string;
            /** @description Password. */
            password: null | string;
        };
        /**
         * @description Output stream of a Python process.
         * @enum {unknown}
         */
        LogStream: "Stdout" | "Stderr";
        /** @description The current user (`GET /me`). */
        MeResponse: {
            /** @description Recorded in the audit. */
            subject: string;
            /** @description Display name. */
            name: string;
            role: null | components["schemas"]["DashboardRole"];
            /** @description Every role the user has (cumulative). */
            roles: components["schemas"]["DashboardRole"][];
            /** @description How the user was identified. */
            mode: components["schemas"]["DashboardAccessMode"];
        };
        /** @description A new stage or pipeline. */
        NewCatalogItem: {
            /** @description Slug (immutable). */
            key: string;
            /** @description Display name. */
            name: string;
            /** @description Description. */
            description?: null | string;
        };
        /** @description Copies a version into a new draft. */
        NewDraftRequest: {
            /**
             * Format: int32
             * @description Version to copy.
             */
            fromVersion: number;
        };
        /** @description Run counters and lists of `GET /overview` (D-115). */
        OverviewResponse: {
            /**
             * Format: date-time
             * @description When it was computed.
             */
            generatedAt: string;
            /** @description Runs created in the last 24 hours, by status. */
            last24Hours: {
                [key: string]: number;
            };
            /** @description Runs created in the 24 hours before, by status. */
            previous24Hours: {
                [key: string]: number;
            };
            /** @description Runs pending, queued, running or cancelling (newest first, up to 50). */
            inProgress: components["schemas"]["RunSummary"][];
            /** @description The 10 latest failed runs (last 7 days). */
            recentFailures: components["schemas"]["RunSummary"][];
            /** @description The 5 next schedule firings. */
            upcomingSchedules: components["schemas"]["UpcomingSchedule"][];
        };
        /** @description A pipeline with its versions and settings. */
        PipelineDetail: {
            /** @description Pipeline key. */
            key: string;
            /** @description Display name. */
            name: string;
            /** @description Description. */
            description: null | string;
            /**
             * Format: int32
             * @description Latest published version.
             */
            latestPublishedVersion: number | null;
            /** @description Every version, newest first. */
            versions: components["schemas"]["VersionSummary"][];
            /** @description Operational settings. */
            settings: components["schemas"]["PipelineSettings"];
            /** @description Retention. */
            retention: components["schemas"]["PipelineRetention"];
            /**
             * Format: uint32
             * @description Concurrency token of the pipeline row.
             */
            rowVersion: number;
            /** @description Creation and last change. */
            authorship: components["schemas"]["Authorship"];
        };
        /** @description The current draft of a pipeline. */
        PipelineDraft: {
            /** @description Pipeline key. */
            key: string;
            /**
             * Format: int32
             * @description Draft version.
             */
            version: number;
            /** @description Content. */
            data: components["schemas"]["PipelineDraftData"];
            /**
             * Format: uint32
             * @description Concurrency token (send it back when saving).
             */
            rowVersion: number;
            /** @description Creation and last change. */
            authorship: components["schemas"]["Authorship"];
        };
        /** @description Editable content of a pipeline version. */
        PipelineDraftData: {
            /** @description Inputs received by the pipeline. */
            inputs?: {
                name: string;
                /** @enum {string} */
                type: "number" | "integer" | "decimal" | "string" | "bool" | "date" | "datetime" | "enum" | "json" | "table" | "file" | "list";
                /** @enum {string} */
                itemType?: "number" | "integer" | "decimal" | "string" | "bool" | "date" | "datetime" | "enum" | "json" | "table" | "file";
                /** @default true */
                required?: boolean;
                /** @description Default value in wire format (not for files and tables). */
                default?: unknown;
                description?: string;
                enumValues?: string[];
                contentTypes?: string[];
                columns?: {
                    name: string;
                    /** @enum {string} */
                    type: "number" | "integer" | "decimal" | "string" | "bool" | "date" | "datetime";
                }[];
            }[];
            /** @description Steps in execution order. */
            steps?: components["schemas"]["PipelineStepDefinition"][];
            /** @description Declared outputs. */
            outputs?: components["schemas"]["PipelineOutputDefinition"][];
            layout?: null | components["schemas"]["JsonNode"];
        };
        /** @description A declared output of the pipeline, taken from an output of a step. */
        PipelineOutputDefinition: {
            /** @description Output name. */
            name: string;
            /** @description Step output that provides the value (its type is inherited). */
            source: components["schemas"]["StepOutputSource"];
            /** @description Description shown in the panel. */
            description?: null | string;
        };
        /** @description Result of publishing a pipeline. */
        PipelinePublication: {
            /** @description The published version. */
            version: components["schemas"]["PipelineVersionDetail"];
            /** @description Validation warnings (they do not block publication). */
            warnings: components["schemas"]["ValidationIssue"][];
        };
        /** @description Retention of a pipeline; it can only be shorter than the global retention. */
        PipelineRetention: {
            /**
             * Format: int32
             * @description Days the files are kept; `null` = global.
             */
            artifactRetentionDays: number | null;
            /**
             * Format: int32
             * @description Days the runs are kept; `null` = global.
             */
            runRetentionDays: number | null;
        };
        /** @description Operational settings of a pipeline (not versioned). */
        PipelineSettings: {
            /**
             * Format: int32
             * @description Maximum simultaneous asynchronous runs; `null` = no limit.
             */
            maxConcurrentRuns: number | null;
        };
        /** @description One step of a pipeline: a fixed stage version and the bindings of its inputs. */
        PipelineStepDefinition: {
            /** @description Step key (slug, unique in the pipeline). */
            key: string;
            /** @description Key of the executed stage. */
            stageKey: string;
            /**
             * Format: int32
             * @description Exact version of the executed stage.
             */
            stageVersion: number;
            /** @description Label shown in the panel. */
            displayName?: null | string;
            /** @description Explicit bindings; unbound inputs use the implicit binding by name. */
            bindings?: components["schemas"]["InputBinding"][];
        };
        /** @description A pipeline in a list. */
        PipelineSummary: {
            /** @description Pipeline key. */
            key: string;
            /** @description Display name. */
            name: string;
            /** @description Description. */
            description: null | string;
            /**
             * Format: int32
             * @description Latest published version.
             */
            latestPublishedVersion: number | null;
            /**
             * Format: int32
             * @description Version of the current draft.
             */
            draftVersion: number | null;
            /**
             * Format: date-time
             * @description Last change.
             */
            updatedAt: string;
        };
        /** @description One version of a pipeline. */
        PipelineVersionDetail: {
            /** @description Pipeline key. */
            key: string;
            /**
             * Format: int32
             * @description Version number.
             */
            version: number;
            /** @description Status. */
            status: components["schemas"]["VersionStatus"];
            /** @description Content. */
            data: components["schemas"]["PipelineDraftData"];
            /**
             * Format: date-time
             * @description Publication time.
             */
            publishedAt?: null | string;
            /** @description Who published. */
            publishedBy?: null | string;
            /**
             * Format: date-time
             * @description Archiving time.
             */
            archivedAt?: null | string;
            /** @description Who archived. */
            archivedBy?: null | string;
            /**
             * Format: uint32
             * @description Concurrency token.
             */
            rowVersion: number;
            /** @description Creation and last change. */
            authorship: components["schemas"]["Authorship"];
        };
        ProblemDetails: {
            type?: null | string;
            title?: null | string;
            /** Format: int32 */
            status?: number | null;
            detail?: null | string;
            instance?: null | string;
        };
        /** @description Global retention in days. */
        RetentionSettings: {
            /**
             * Format: double
             * @description File contents.
             */
            artifactDays: number;
            /**
             * Format: double
             * @description Runs.
             */
            runDays: number;
            /**
             * Format: double
             * @description Audit events.
             */
            auditDays: number;
        };
        /** @description Automatic retry policy of a stage. */
        RetryPolicy: {
            /**
             * Format: int32
             * @description Number of retries after the first attempt (0 = no retry).
             */
            maxAttempts: number;
            /** @description Delay before the first retry. */
            initialDelay: string;
            /**
             * Format: double
             * @description Multiplier applied to the delay on each further retry.
             */
            backoffMultiplier: number;
        };
        /** @description Why a run, step or attempt failed. */
        RunError: {
            /** @description Failure classification. */
            kind: components["schemas"]["ErrorKind"];
            /** @description Human-readable message. */
            message: string;
            /** @description Stable code (e.g. a `StageError` code or `UNDECLARED_OUTPUT`). */
            code?: null | string;
            /** @description Key of the step that failed. */
            stepKey?: null | string;
            /** @description Python traceback, when available. */
            traceback?: null | string;
            details?: null | components["schemas"]["JsonNode"];
        };
        /** @description A queued run. */
        RunHandle: {
            /** @description Run identifier. */
            runId: components["schemas"]["RunId"];
            /** @description RunStatus.Queued or RunStatus.Pending (the current status when deduplicated). */
            status: components["schemas"]["RunStatus"];
            /** @description Whether an earlier run with the same idempotency key was returned. */
            deduplicated: boolean;
        };
        /**
         * Format: uuid
         * @description Identifier of a run (time-ordered UUID v7). Its timestamp is the creation time of the run.
         */
        RunId: string;
        /**
         * @description What a run executes.
         * @enum {unknown}
         */
        RunKind: "Pipeline" | "PipelineTest" | "Stage" | "StageTest";
        /** @description A persisted run with its steps, attempts and files. */
        RunSnapshot: {
            /** @description Run identifier. */
            id: components["schemas"]["RunId"];
            /** @description What was executed. */
            kind: components["schemas"]["RunKind"];
            /** @description Sync or async. */
            mode: components["schemas"]["ExecutionMode"];
            /** @description Status. */
            status: components["schemas"]["RunStatus"];
            /** @description Pipeline or stage key. */
            targetKey: string;
            /**
             * Format: int32
             * @description Pipeline or stage version.
             */
            targetVersion: number;
            /** @description How the run started. */
            trigger: components["schemas"]["RunTrigger"];
            /** @description Who started it. */
            triggeredBy: string;
            /** @description Validated inputs; files as `{"$artifact": "&lt;id&gt;"}`. */
            inputs: components["schemas"]["JsonObject"];
            outputs?: null | components["schemas"]["JsonObject"];
            error?: null | components["schemas"]["RunError"];
            /** @description Idempotency key given by the caller. */
            idempotencyKey?: null | string;
            /** @description Correlation given by the caller (returned in the events). */
            correlationId?: null | string;
            scheduleId?: null | components["schemas"]["ScheduleId"];
            triggerDetails?: null | components["schemas"]["JsonObject"];
            /**
             * Format: date-time
             * @description Creation time.
             */
            createdAt: string;
            /**
             * Format: date-time
             * @description When it was queued.
             */
            queuedAt?: null | string;
            /**
             * Format: date-time
             * @description Start time.
             */
            startedAt?: null | string;
            /**
             * Format: date-time
             * @description End time.
             */
            finishedAt?: null | string;
            /**
             * Format: date-time
             * @description When cancellation was requested.
             */
            cancelRequestedAt?: null | string;
            /** @description Who requested cancellation. */
            cancelRequestedBy?: null | string;
            /**
             * Format: date-time
             * @description When the run is deleted (pipelines with a shorter retention).
             */
            expiresAt?: null | string;
            /** @description Steps in order. */
            steps: components["schemas"]["StepRunSnapshot"][];
            /** @description Input and output files. */
            artifacts: components["schemas"]["ArtifactInfo"][];
        };
        /**
         * @description Status of a run.
         * @enum {unknown}
         */
        RunStatus: "Pending" | "Queued" | "Running" | "Succeeded" | "Failed" | "Cancelling" | "Cancelled";
        /** @description A run in a list. */
        RunSummary: {
            /** @description Run identifier. */
            id: components["schemas"]["RunId"];
            /** @description What was executed. */
            kind: components["schemas"]["RunKind"];
            /** @description Sync or async. */
            mode: components["schemas"]["ExecutionMode"];
            /** @description Status. */
            status: components["schemas"]["RunStatus"];
            /** @description Pipeline or stage key. */
            targetKey: string;
            /**
             * Format: int32
             * @description Pipeline or stage version.
             */
            targetVersion: number;
            /** @description How the run started. */
            trigger: components["schemas"]["RunTrigger"];
            /** @description Who started it. */
            triggeredBy: string;
            /**
             * Format: date-time
             * @description Creation time.
             */
            createdAt: string;
            /**
             * Format: date-time
             * @description Start time.
             */
            startedAt: null | string;
            /**
             * Format: date-time
             * @description End time.
             */
            finishedAt: null | string;
            error: null | components["schemas"]["RunError"];
        };
        /**
         * @description Who or what started a run.
         * @enum {unknown}
         */
        RunTrigger: "Library" | "Dashboard" | "Schedule" | "QueueCommand";
        /** @description Settings of a cron schedule of a pipeline. */
        ScheduleData: {
            /** @description Display name (1-200 chars). */
            name: string;
            /** @description Cron expression with 5 fields (minute precision) or 6 fields (with seconds). */
            cron: string;
            /** @description IANA time zone in which the expression is evaluated (daylight saving time handled). */
            timeZone?: string;
            /**
             * Format: int32
             * @description Version to run; `null` runs the latest published version at each firing.
             */
            pinnedVersion?: number | null;
            /** @description Fixed inputs of the pipeline (no files). */
            inputs?: components["schemas"]["JsonObject"];
            /** @description Whether the schedule fires. */
            isEnabled?: boolean;
        };
        /** @description A stored schedule. */
        ScheduleDetail: {
            /** @description Identifier. */
            id: components["schemas"]["ScheduleId"];
            /** @description Scheduled pipeline. */
            pipelineKey: string;
            /** @description Settings. */
            data: components["schemas"]["ScheduleData"];
            /**
             * Format: date-time
             * @description Next firing (UTC); `null` when disabled.
             */
            nextRunAt?: null | string;
            /**
             * Format: date-time
             * @description Last firing.
             */
            lastFiredAt?: null | string;
            lastRunId?: null | components["schemas"]["RunId"];
            /** @description Why the last firing was rejected (cleared by the next successful one). */
            lastError?: null | string;
            /**
             * Format: uint32
             * @description Row version for optimistic concurrency.
             */
            rowVersion: number;
            /** @description Who created and last changed it. */
            authorship: components["schemas"]["Authorship"];
        };
        /**
         * Format: uuid
         * @description Identifier of a schedule (UUID v7).
         */
        ScheduleId: string;
        /** @description One occurrence of a schedule. */
        ScheduleOccurrence: {
            /**
             * Format: date-time
             * @description Instant in UTC.
             */
            utc: string;
            /**
             * Format: date-time
             * @description Same instant in the schedule time zone (with its offset).
             */
            local: string;
        };
        /** @description Next occurrences of a cron expression. */
        SchedulePreviewRequest: {
            /** @description Cron expression (5 or 6 fields). */
            cron: string;
            /** @description IANA time zone (default `America/Sao_Paulo`). */
            timeZone: null | string;
            /**
             * Format: int32
             * @description How many (1-20; default 5).
             */
            count: number | null;
        };
        /** @description Status of a secret declared by stages (never its value). */
        SecretStatus: {
            /** @description Secret name. */
            name: string;
            /** @description Stage versions that declare it (`key@version`). */
            declaredBy: string[];
            /** @description Whether the secret provider returns a value. */
            resolved: boolean;
        };
        /** @description Read-only settings (`GET /admin/settings`). */
        SettingsResponse: {
            /** @description Global retention. */
            retention: components["schemas"]["RetentionSettings"];
            /** @description Limits of runs and uploads. */
            limits: components["schemas"]["LimitSettings"];
            /** @description Access mode of the dashboard. */
            access: components["schemas"]["AuthInfo"];
            /** @description Versions of the components. */
            versions: {
                [key: string]: string;
            };
        };
        /** @description A stage with its versions and usages. */
        StageDetail: {
            /** @description Stage key. */
            key: string;
            /** @description Display name. */
            name: string;
            /** @description Description. */
            description: null | string;
            /**
             * Format: int32
             * @description Latest published version.
             */
            latestPublishedVersion: number | null;
            /** @description Every version, newest first. */
            versions: components["schemas"]["VersionSummary"][];
            /** @description Pipeline steps that use a version of the stage. */
            usedBy: components["schemas"]["StageUsage"][];
            /**
             * Format: uint32
             * @description Concurrency token of the stage row.
             */
            rowVersion: number;
            /** @description Creation and last change. */
            authorship: components["schemas"]["Authorship"];
        };
        /** @description The current draft of a stage. */
        StageDraft: {
            /** @description Stage key. */
            key: string;
            /**
             * Format: int32
             * @description Draft version.
             */
            version: number;
            /** @description Content. */
            data: components["schemas"]["StageDraftData"];
            /**
             * Format: uint32
             * @description Concurrency token (send it back when saving).
             */
            rowVersion: number;
            /** @description Creation and last change. */
            authorship: components["schemas"]["Authorship"];
        };
        /** @description Editable content of a stage version. */
        StageDraftData: {
            /** @description Python source of the single script. */
            script: string;
            /** @description Python version (e.g. `3.12`). */
            pythonVersion: string;
            /** @description Dependencies in requirements.txt format. */
            requirements?: null | string;
            /** @description Declared inputs. */
            inputs?: {
                name: string;
                /** @enum {string} */
                type: "number" | "integer" | "decimal" | "string" | "bool" | "date" | "datetime" | "enum" | "json" | "table" | "file" | "list";
                /** @enum {string} */
                itemType?: "number" | "integer" | "decimal" | "string" | "bool" | "date" | "datetime" | "enum" | "json" | "table" | "file";
                /** @default true */
                required?: boolean;
                /** @description Default value in wire format (not for files and tables). */
                default?: unknown;
                description?: string;
                enumValues?: string[];
                contentTypes?: string[];
                columns?: {
                    name: string;
                    /** @enum {string} */
                    type: "number" | "integer" | "decimal" | "string" | "bool" | "date" | "datetime";
                }[];
            }[];
            /** @description Declared outputs. */
            outputs?: {
                name: string;
                /** @enum {string} */
                type: "number" | "integer" | "decimal" | "string" | "bool" | "date" | "datetime" | "enum" | "json" | "table" | "file" | "list";
                /** @enum {string} */
                itemType?: "number" | "integer" | "decimal" | "string" | "bool" | "date" | "datetime" | "enum" | "json" | "table" | "file";
                /** @default true */
                required?: boolean;
                /** @description Default value in wire format (not for files and tables). */
                default?: unknown;
                description?: string;
                enumValues?: string[];
                contentTypes?: string[];
                columns?: {
                    name: string;
                    /** @enum {string} */
                    type: "number" | "integer" | "decimal" | "string" | "bool" | "date" | "datetime";
                }[];
            }[];
            /** @description Names of the secrets the script needs. */
            requiredSecrets?: string[];
            /** @description Maximum duration of one attempt. */
            timeout?: string;
            /**
             * Format: int32
             * @description Memory limit in MB; `null` uses the runner default.
             */
            memoryLimitMb?: number | null;
            /** @description Automatic retry policy. */
            retry?: components["schemas"]["RetryPolicy"];
            /** @description Whether running the script twice with the same inputs is safe. */
            idempotent?: boolean;
        };
        /** @description A stage in a list. */
        StageSummary: {
            /** @description Stage key. */
            key: string;
            /** @description Display name. */
            name: string;
            /** @description Description. */
            description: null | string;
            /**
             * Format: int32
             * @description Latest published version.
             */
            latestPublishedVersion: number | null;
            /**
             * Format: int32
             * @description Version of the current draft.
             */
            draftVersion: number | null;
            /**
             * Format: date-time
             * @description Last change.
             */
            updatedAt: string;
            /**
             * Format: int32
             * @description Distinct pipelines with a published version that uses any version of the stage.
             */
            usedByPipelines: number;
        };
        /** @description A pipeline step that uses a stage version. */
        StageUsage: {
            /** @description Pipeline key. */
            pipelineKey: string;
            /**
             * Format: int32
             * @description Pipeline version.
             */
            pipelineVersion: number;
            /** @description Status of the pipeline version. */
            pipelineStatus: components["schemas"]["VersionStatus"];
            /** @description Step key. */
            stepKey: string;
            /**
             * Format: int32
             * @description Stage version used by the step.
             */
            stageVersion: number;
        };
        /** @description One version of a stage. */
        StageVersionDetail: {
            /** @description Stage key. */
            key: string;
            /**
             * Format: int32
             * @description Version number.
             */
            version: number;
            /** @description Status. */
            status: components["schemas"]["VersionStatus"];
            /** @description Content. */
            data: components["schemas"]["StageDraftData"];
            /** @description SHA-256 of the script. */
            scriptSha256: string;
            /** @description Locked dependencies (published versions). */
            lockedRequirements?: null | string;
            /** @description Hash of the Python environment (published versions). */
            environmentHash?: null | string;
            /**
             * Format: date-time
             * @description Publication time.
             */
            publishedAt?: null | string;
            /** @description Who published. */
            publishedBy?: null | string;
            /**
             * Format: date-time
             * @description Archiving time.
             */
            archivedAt?: null | string;
            /** @description Who archived. */
            archivedBy?: null | string;
            /**
             * Format: uint32
             * @description Concurrency token.
             */
            rowVersion: number;
            /** @description Creation and last change. */
            authorship: components["schemas"]["Authorship"];
        };
        /** @description An output of an earlier step (`$steps.&lt;step&gt;.&lt;field&gt;`). */
        StepOutputSource: {
            /** @description Step key. */
            step: string;
            /** @description Output name of that step. */
            field: string;
        };
        /**
         * Format: uuid
         * @description Identifier of a step within a run (time-ordered UUID v7).
         */
        StepRunId: string;
        /** @description A persisted step of a run. */
        StepRunSnapshot: {
            /** @description Step identifier. */
            id: components["schemas"]["StepRunId"];
            /** @description Step key (the stage key for single-stage runs). */
            stepKey: string;
            /**
             * Format: int32
             * @description 1-based position.
             */
            order: number;
            /** @description Executed stage. */
            stageKey: string;
            /**
             * Format: int32
             * @description Exact stage version.
             */
            stageVersion: number;
            /** @description Status. */
            status: components["schemas"]["StepStatus"];
            /**
             * Format: int32
             * @description Number of the last attempt (0 = not executed).
             */
            currentAttempt: number;
            inputs?: null | components["schemas"]["JsonObject"];
            outputs?: null | components["schemas"]["JsonObject"];
            error?: null | components["schemas"]["RunError"];
            /**
             * Format: date-time
             * @description Start time.
             */
            startedAt?: null | string;
            /**
             * Format: date-time
             * @description End time.
             */
            finishedAt?: null | string;
            /** @description Attempts in order. */
            attempts: components["schemas"]["AttemptSnapshot"][];
        };
        /**
         * @description Status of a step within a run.
         * @enum {unknown}
         */
        StepStatus: "Pending" | "Queued" | "Running" | "Succeeded" | "Failed" | "TimedOut" | "Cancelled" | "Skipped";
        /** @description A next schedule firing. */
        UpcomingSchedule: {
            /** @description Schedule identifier. */
            id: components["schemas"]["ScheduleId"];
            /** @description Schedule name. */
            name: string;
            /** @description Scheduled pipeline. */
            pipelineKey: string;
            /**
             * Format: date-time
             * @description Next firing (UTC).
             */
            nextRunAt: string;
        };
        /** @description One validation problem. */
        ValidationIssue: {
            /** @description Stable code (e.g. `PIPE_TYPE_MISMATCH`). */
            code: string;
            /** @description Human-readable message. */
            message: string;
            /** @description Step key, when the problem belongs to a step. */
            step?: null | string;
            /** @description Field name (step input, pipeline input or output). */
            field?: null | string;
        };
        /** @description Result of a pipeline validation. */
        ValidationReport: {
            /** @description Problems that block publication. */
            errors: components["schemas"]["ValidationIssue"][];
            /** @description Problems that do not block publication. */
            warnings: components["schemas"]["ValidationIssue"][];
            /** @description Whether there are no errors. */
            isValid?: boolean;
        };
        /** @enum {unknown} */
        VersionStatus: "Draft" | "Published" | "Archived";
        /** @description A version in a list. */
        VersionSummary: {
            /**
             * Format: int32
             * @description Version number.
             */
            version: number;
            /** @description Status. */
            status: components["schemas"]["VersionStatus"];
            /**
             * Format: date-time
             * @description Publication time.
             */
            publishedAt: null | string;
            /**
             * Format: date-time
             * @description Last change.
             */
            updatedAt: string;
        };
    };
    responses: never;
    parameters: never;
    requestBodies: never;
    headers: never;
    pathItems: never;
}
export type $defs = Record<string, never>;
export interface operations {
    GetAuth: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AuthInfo"];
                };
            };
        };
    };
    Login: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: {
            content: {
                "application/json": null | components["schemas"]["LoginRequest"];
            };
        };
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    Logout: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetMe: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["MeResponse"];
                };
            };
        };
    };
    Import: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ImportReport"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Payload Too Large */
            413: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetOverview: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OverviewResponse"];
                };
            };
        };
    };
    DownloadArtifact: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Gone */
            410: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ListAudit: {
        parameters: {
            query?: {
                actor?: string;
                action?: string;
                entityType?: string;
                entityKey?: string;
                from?: string;
                to?: string;
                cursor?: string;
                limit?: number;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["KeysetPageOfAuditEvent"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ListSecrets: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SecretStatus"][];
                };
            };
        };
    };
    GetSettings: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SettingsResponse"];
                };
            };
        };
    };
    GetFieldTypes: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["FieldTypesResponse"];
                };
            };
        };
    };
    ListStages: {
        parameters: {
            query?: {
                search?: string;
                status?: components["schemas"]["VersionStatus"];
                pipelineKey?: string;
                page?: number;
                pageSize?: number;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ApiPageOfStageSummary"];
                };
            };
        };
    };
    CreateStage: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["NewCatalogItem"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StageDetail"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetStage: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StageDetail"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    DeleteStage: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    UpdateStage: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CatalogItemChanges"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StageDetail"];
                };
            };
            /** @description Precondition Failed */
            412: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Precondition Required */
            428: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetStageVersion: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
                version: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StageVersionDetail"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetStageDraft: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StageDraft"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    SaveStageDraft: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["StageDraftData"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StageDraft"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Precondition Failed */
            412: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    CreateStageDraft: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["NewDraftRequest"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StageDraft"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    DiscardStageDraft: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Precondition Failed */
            412: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ValidateStageDraft: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ValidationReport"];
                };
            };
        };
    };
    TestStageDraft: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Accepted */
            202: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["RunHandle"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Payload Too Large */
            413: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    PublishStage: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StageVersionDetail"];
                };
            };
            /** @description Precondition Failed */
            412: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Gateway Timeout */
            504: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ArchiveStageVersion: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
                version: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["StageVersionDetail"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ExportStageVersion: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
                version: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    RunStage: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Accepted */
            202: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["RunHandle"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Payload Too Large */
            413: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ListPipelines: {
        parameters: {
            query?: {
                search?: string;
                status?: components["schemas"]["VersionStatus"];
                page?: number;
                pageSize?: number;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ApiPageOfPipelineSummary"];
                };
            };
        };
    };
    CreatePipeline: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["NewCatalogItem"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PipelineDetail"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ValidatePipeline: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PipelineDraftData"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ValidationReport"];
                };
            };
        };
    };
    GetPipeline: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PipelineDetail"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    DeletePipeline: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetPipelineVersion: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
                version: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PipelineVersionDetail"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetPipelineDraft: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PipelineDraft"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    SavePipelineDraft: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PipelineDraftData"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PipelineDraft"];
                };
            };
            /** @description Precondition Failed */
            412: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    CreatePipelineDraft: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["NewDraftRequest"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PipelineDraft"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    DiscardPipelineDraft: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Precondition Failed */
            412: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    TestPipelineDraft: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Accepted */
            202: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["RunHandle"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Payload Too Large */
            413: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    PublishPipeline: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PipelinePublication"];
                };
            };
            /** @description Precondition Failed */
            412: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ArchivePipelineVersion: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
                version: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PipelineVersionDetail"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    UpdatePipelineSettings: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PipelineSettings"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PipelineDetail"];
                };
            };
            /** @description Precondition Failed */
            412: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    UpdatePipelineRetention: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["PipelineRetention"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PipelineDetail"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Precondition Failed */
            412: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ExportPipelineVersion: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
                version: number;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    RunPipeline: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Accepted */
            202: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["RunHandle"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Payload Too Large */
            413: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    CreateSchedule: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                key: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ScheduleData"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ScheduleDetail"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ListRuns: {
        parameters: {
            query?: {
                pipelineKey?: string;
                stageKey?: string;
                kind?: components["schemas"]["RunKind"];
                status?: components["schemas"]["RunStatus"];
                trigger?: components["schemas"]["RunTrigger"];
                from?: string;
                to?: string;
                cursor?: string;
                limit?: number;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["KeysetPageOfRunSummary"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetRun: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["RunSnapshot"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    GetAttemptLog: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                attemptId: string;
                stream: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": string;
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    CancelRun: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Accepted */
            202: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ReprocessRun: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Accepted */
            202: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["RunHandle"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    ListSchedules: {
        parameters: {
            query?: {
                pipelineKey?: string;
                page?: number;
                pageSize?: number;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ApiPageOfScheduleDetail"];
                };
            };
        };
    };
    UpdateSchedule: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ScheduleData"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ScheduleDetail"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Precondition Failed */
            412: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    DeleteSchedule: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Precondition Failed */
            412: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    EnableSchedule: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ScheduleDetail"];
                };
            };
        };
    };
    DisableSchedule: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ScheduleDetail"];
                };
            };
        };
    };
    PreviewSchedule: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["SchedulePreviewRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ScheduleOccurrence"][];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
}
