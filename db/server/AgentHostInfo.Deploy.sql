-- What an agent reports about its host when it registers, for the WebUI's agents page, as one JSON
-- document rather than a column per detail: new details can be added later without a schema change.
-- NULL for rows registered before this existed. Keys are camelCase; today:
--   osDescription, osArchitecture, processorCount,
--   ipAddresses (an array, since a host can have several IPv4 and IPv6 addresses),
--   startedAt   (ISO 8601, when the agent process started, so uptime is "now - startedAt").
ALTER TABLE "Agents" ADD COLUMN "Info" jsonb NULL;
