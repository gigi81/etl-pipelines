-- Which agents may run a pipeline. A pipeline with no rows here runs on any agent; otherwise it runs on an
-- agent that has any of its tags (PipelineAgentTags) or that is named explicitly (PipelineAgents).
--
-- Keyed by (package, pipeline name), like PipelineDisplayNames, not by the Pipelines row: installing a newer
-- version of a package creates new Pipelines rows, and where a pipeline runs should survive an update.
CREATE TABLE "PipelineAgentTags"
(
    "PackageId"    uuid NOT NULL,
    "PipelineName" text NOT NULL,
    "Tag"          text NOT NULL,
    PRIMARY KEY ("PackageId", "PipelineName", "Tag"),
    CONSTRAINT "FK_PipelineAgentTags_Packages" FOREIGN KEY ("PackageId") REFERENCES "Packages" ("Id")
);

CREATE TABLE "PipelineAgents"
(
    "PackageId"    uuid NOT NULL,
    "PipelineName" text NOT NULL,
    "AgentId"      uuid NOT NULL,
    PRIMARY KEY ("PackageId", "PipelineName", "AgentId"),
    CONSTRAINT "FK_PipelineAgents_Packages" FOREIGN KEY ("PackageId") REFERENCES "Packages" ("Id")
    -- No foreign key to Agents: an agent is written to its table a few seconds after it registers (see the
    -- live state), and naming it here must not fail in those seconds. An id that matches no agent is ignored.
);
