-- Host details an agent reports once, when it registers, for the WebUI's agents page. All of them
-- are nullable: rows registered before this change, and agents older than it, never reported them.
-- IpAddresses is the host's unicast addresses, as text; StartedAt is when the agent process
-- started, so the UI can show uptime as "now - StartedAt".
ALTER TABLE "Agents" ADD COLUMN "OsDescription"   text        NULL;
ALTER TABLE "Agents" ADD COLUMN "OsArchitecture"  text        NULL;
ALTER TABLE "Agents" ADD COLUMN "IpAddresses"     text[]      NULL;
ALTER TABLE "Agents" ADD COLUMN "ProcessorCount"  integer     NULL;
ALTER TABLE "Agents" ADD COLUMN "StartedAt"       timestamptz NULL;
