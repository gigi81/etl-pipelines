import type { Pipeline } from '../api/types';

/** What identifies a pipeline across versions of its package: the package and the pipeline's own name. */
const keyOf = (packageId: string, name: string) => `${packageId}\u0000${name}`;

/**
 * How to show a pipeline: the display name a user gave it, or - if none - its own name. Built from
 * the pipelines list, so a run (which only knows its package and the pipeline's own name) can be
 * labelled the same way, and a rename shows everywhere at once.
 */
export function buildPipelineLabeller(pipelines: Pipeline[] | undefined) {
  const names = new Map((pipelines ?? []).filter((p) => p.displayName).map((p) => [keyOf(p.packageId, p.name), p.displayName!]));
  return (packageId: string, name: string) => names.get(keyOf(packageId, name)) ?? name;
}

/** The label of a pipeline in the list, which already knows its display name. */
export const pipelineLabel = (pipeline: Pick<Pipeline, 'name' | 'displayName'>) => pipeline.displayName ?? pipeline.name;
