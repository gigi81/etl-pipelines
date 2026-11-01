-- Tags a user gave an agent in the WebUI. "Tags" stays what the agent reports about itself each time it
-- registers (its Agent:Tags setting), so it is replaced at every start; these are kept, which is why they
-- are a column of their own. An agent's tags, for matching it to pipelines, are both lists together.
ALTER TABLE "Agents" ADD COLUMN "AssignedTags" text[] NOT NULL DEFAULT '{}';
