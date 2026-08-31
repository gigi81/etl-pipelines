-- The schema EtlPipelines.Server.Database's DbContext reads and writes through - see that
-- project's IEntityTypeConfiguration<T> classes for the EF Core side of this mapping, and
-- SERVER.md's "Database schema management" decision for why dbdeploy, not `dotnet ef
-- migrations`, owns it. Column names are PascalCase and quoted throughout to match Npgsql's own
-- default identifier convention for EF Core - the two sides agree without a naming-convention
-- translation layer on either one.
--
-- No database-generated defaults for "Id": every row's id is a Guid the application already knows
-- before it inserts - most visibly Runs.Id, which is the server-issued session id a pipeline
-- process is handed before this row exists at all - so there is nothing for the database to
-- default it to.
--
-- Created in dependency order (a table only ever references one already created above it).
-- _Init.sql - the baseline every later change is layered onto - has no rollback counterpart, the
-- same way every other database in this dbdeploy install (see the examples in the dbdeploy repo
-- itself) has none for its own: rolling it back would mean dropping the schema entirely, which is
-- never what "undo the last change" should do once there have been changes since. A schema
-- change from here on is a normal, rollback-capable Deploy/Rollback pair.

CREATE TABLE "Packages"
(
    "Id"             uuid        NOT NULL PRIMARY KEY,
    "NugetPackageId" text        NOT NULL,
    "CreatedAt"      timestamptz NOT NULL,
    CONSTRAINT "UX_Packages_NugetPackageId" UNIQUE ("NugetPackageId")
);

-- Status: Installing/Installed/Failed.
CREATE TABLE "PackageVersions"
(
    "Id"          uuid        NOT NULL PRIMARY KEY,
    "PackageId"   uuid        NOT NULL,
    "Version"     text        NOT NULL,
    "InstalledAt" timestamptz NOT NULL,
    "Status"      text        NOT NULL,
    CONSTRAINT "FK_PackageVersions_Packages" FOREIGN KEY ("PackageId") REFERENCES "Packages" ("Id"),
    CONSTRAINT "UX_PackageVersions_PackageId_Version" UNIQUE ("PackageId", "Version")
);
CREATE INDEX "IX_PackageVersions_PackageId" ON "PackageVersions" ("PackageId");

-- One row per pipeline WITHIN a package - EtlPipelines.Samples.ArchiveToDatabase already proves
-- one package can register more than one (build-feed and archive), so this is a child of
-- PackageVersions, not Packages directly. ListInstalledPipelines/ExecutePipeline both operate at
-- this granularity.
CREATE TABLE "Pipelines"
(
    "Id"               uuid        NOT NULL PRIMARY KEY,
    "PackageVersionId" uuid        NOT NULL,
    "Name"             text        NOT NULL,
    "CreatedAt"        timestamptz NOT NULL,
    CONSTRAINT "FK_Pipelines_PackageVersions" FOREIGN KEY ("PackageVersionId") REFERENCES "PackageVersions" ("Id"),
    CONSTRAINT "UX_Pipelines_PackageVersionId_Name" UNIQUE ("PackageVersionId", "Name")
);
CREATE INDEX "IX_Pipelines_PackageVersionId" ON "Pipelines" ("PackageVersionId");

-- Status: Online/Offline (Phase 8 flips this after N missed heartbeats).
CREATE TABLE "Agents"
(
    "Id"              uuid        NOT NULL PRIMARY KEY,
    "MachineName"     text        NOT NULL,
    "Tags"            text[]      NOT NULL DEFAULT '{}',
    "Version"         text        NOT NULL,
    "Status"          text        NOT NULL,
    "LastHeartbeatAt" timestamptz NOT NULL
);

-- Id is the session id (see the header above). AgentId is nullable: a run is queued before it is
-- dispatched to any agent. Status: Queued/Dispatched/Running/Succeeded/Failed/AgentLost.
CREATE TABLE "Runs"
(
    "Id"          uuid        NOT NULL PRIMARY KEY,
    "PipelineId"  uuid        NOT NULL,
    "AgentId"     uuid        NULL,
    "Status"      text        NOT NULL,
    "RequestedAt" timestamptz NOT NULL,
    "StartedAt"   timestamptz NULL,
    "CompletedAt" timestamptz NULL,
    "ExitCode"    integer     NULL,
    "RowsRead"    bigint      NULL,
    "RowsWritten" bigint      NULL,
    "RowsFailed"  bigint      NULL,
    CONSTRAINT "FK_Runs_Pipelines" FOREIGN KEY ("PipelineId") REFERENCES "Pipelines" ("Id"),
    CONSTRAINT "FK_Runs_Agents" FOREIGN KEY ("AgentId") REFERENCES "Agents" ("Id")
);
CREATE INDEX "IX_Runs_PipelineId" ON "Runs" ("PipelineId");
CREATE INDEX "IX_Runs_AgentId" ON "Runs" ("AgentId");

CREATE TABLE "StageResults"
(
    "Id"               uuid    NOT NULL PRIMARY KEY,
    "RunId"            uuid    NOT NULL,
    "Sequence"         integer NOT NULL,
    "Name"             text    NOT NULL,
    "RowsIn"           bigint  NOT NULL,
    "RowsOut"          bigint  NOT NULL,
    "RowsFailed"       bigint  NOT NULL,
    "ElapsedMs"        bigint  NOT NULL,
    "ErrorCode"        text    NULL,
    "ErrorDescription" text    NULL,
    CONSTRAINT "FK_StageResults_Runs" FOREIGN KEY ("RunId") REFERENCES "Runs" ("Id"),
    CONSTRAINT "UX_StageResults_RunId_Sequence" UNIQUE ("RunId", "Sequence")
);
CREATE INDEX "IX_StageResults_RunId" ON "StageResults" ("RunId");

-- Sampled on a timer by the agent's ResourceMonitor (Phase 5/8) off
-- Process.TotalProcessorTime/WorkingSet64.
CREATE TABLE "AgentResourceSamples"
(
    "Id"              uuid             NOT NULL PRIMARY KEY,
    "RunId"           uuid             NOT NULL,
    "AgentId"         uuid             NOT NULL,
    "SampledAt"       timestamptz      NOT NULL,
    "CpuPercent"      double precision NOT NULL,
    "WorkingSetBytes" bigint           NOT NULL,
    CONSTRAINT "FK_AgentResourceSamples_Runs" FOREIGN KEY ("RunId") REFERENCES "Runs" ("Id"),
    CONSTRAINT "FK_AgentResourceSamples_Agents" FOREIGN KEY ("AgentId") REFERENCES "Agents" ("Id")
);
CREATE INDEX "IX_AgentResourceSamples_RunId" ON "AgentResourceSamples" ("RunId");
CREATE INDEX "IX_AgentResourceSamples_AgentId" ON "AgentResourceSamples" ("AgentId");

-- Flat key-value, mirroring IConfiguration's own colon-path shape deliberately (e.g.
-- "ConnectionStrings:sales") - what lets GetConfigurationResponse.entries (Phase 2) feed straight
-- into a ConfigurationProvider with zero translation. EncryptedValue is never plaintext - Phase
-- 4's SecretsStore wraps IDataProtector around every read and write.
CREATE TABLE "ConfigurationEntries"
(
    "Id"             uuid        NOT NULL PRIMARY KEY,
    "Key"            text        NOT NULL,
    "EncryptedValue" bytea       NOT NULL,
    "UpdatedAt"      timestamptz NOT NULL,
    CONSTRAINT "UX_ConfigurationEntries_Key" UNIQUE ("Key")
);

-- Always exactly one row in phase 1 - the local bagetter URL. Ordinal is kept for a possible
-- future multi-feed phase; unused while this table has one row. See SERVER.md's "Package feed"
-- decision for why this is not a list today.
CREATE TABLE "NuGetFeeds"
(
    "Id"      uuid    NOT NULL PRIMARY KEY,
    "Url"     text    NOT NULL,
    "Ordinal" integer NOT NULL
);
