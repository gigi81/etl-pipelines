import type { Agent, PipelineAssignment } from '../api/types';

/** Nothing assigned: any agent may run the pipeline. */
export const isAnyAgent = (assignment: PipelineAssignment): boolean =>
  assignment.tags.length === 0 && assignment.agentIds.length === 0;

/**
 * Whether an agent may run a pipeline with this assignment: it is named, or has any one of the tags
 * (compared ignoring case). The server decides this when a run is started; this mirrors it, to show
 * who would be eligible.
 */
export function allows(assignment: PipelineAssignment, agent: Agent): boolean {
  if (isAnyAgent(assignment) || assignment.agentIds.includes(agent.id)) return true;
  const wanted = new Set(assignment.tags.map((tag) => tag.toLowerCase()));
  return agent.tags.some((tag) => wanted.has(tag.toLowerCase()));
}

/** The agents that may run a pipeline with this assignment. */
export const eligibleAgents = (assignment: PipelineAssignment, agents: Agent[]): Agent[] =>
  agents.filter((agent) => allows(assignment, agent));

/** Every tag any agent has, lower case, without repeats and sorted - what to suggest when typing one. */
export function knownTags(agents: Agent[]): string[] {
  return [...new Set(agents.flatMap((agent) => agent.tags.map((tag) => tag.toLowerCase())))].sort();
}

/**
 * Where a pipeline may run, in words: `any agent`, or its tags and the names of the agents it is
 * assigned to - `gpu, eu, build-01`.
 */
export function describeAssignment(assignment: PipelineAssignment, agents: Agent[]): string {
  if (isAnyAgent(assignment)) return 'any agent';
  const names = assignment.agentIds.map((id) => agents.find((agent) => agent.id === id)?.machineName ?? 'unknown agent');
  return [...assignment.tags, ...names].join(', ');
}
