-- Exact reverse of InitialSchema.CreateCoreTables.Deploy.sql's creation order, so every foreign
-- key referenced by a table dropped earlier is already gone by the time its own table drops.

DROP TABLE "NuGetFeeds";
DROP TABLE "ConfigurationEntries";
DROP TABLE "AgentResourceSamples";
DROP TABLE "StageResults";
DROP TABLE "Runs";
DROP TABLE "Agents";
DROP TABLE "Pipelines";
DROP TABLE "PackageVersions";
DROP TABLE "Packages";
