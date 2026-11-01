-- A display name a user gave a pipeline in the WebUI. The pipeline's own name is what its package
-- registers and what an agent launches it by, so it cannot change; this is only how the UI labels it.
--
-- Keyed by (package, pipeline name), not by the Pipelines row: installing a newer version of a package
-- creates new Pipelines rows, and a display name should survive an update.
CREATE TABLE "PipelineDisplayNames"
(
    "PackageId"    uuid        NOT NULL,
    "PipelineName" text        NOT NULL,
    "DisplayName"  text        NOT NULL,
    "UpdatedAt"    timestamptz NOT NULL,
    PRIMARY KEY ("PackageId", "PipelineName"),
    CONSTRAINT "FK_PipelineDisplayNames_Packages" FOREIGN KEY ("PackageId") REFERENCES "Packages" ("Id")
);
