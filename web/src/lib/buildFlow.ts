import type { RunDetail } from '../api/types';
import { isActive } from './format';

export type NodeState = 'done' | 'failed' | 'running' | 'pending' | 'skipped';

export interface FlowNode {
  key: string;
  name: string;
  state: NodeState;
  rowsIn?: number;
  rowsOut?: number;
  rowsFailed?: number;
  elapsedMs?: number;
  error?: string;
}

/**
 * The nodes to draw for a run: the stages that have completed, then - from the stages of the
 * pipeline's last successful run - the ones still to come. The first of those is "running" while
 * the run is; once the run has ended without reaching them they are "skipped".
 */
export function buildFlow({ run, stages, expectedStages }: RunDetail): FlowNode[] {
  const active = isActive(run.status);

  const nodes: FlowNode[] = stages.map((stage) => ({
    key: `stage-${stage.sequence}`,
    name: stage.name,
    state: stage.errorCode ? 'failed' : 'done',
    rowsIn: stage.rowsIn,
    rowsOut: stage.rowsOut,
    rowsFailed: stage.rowsFailed,
    elapsedMs: stage.elapsedMs,
    error: stage.errorCode ? [stage.errorCode, stage.errorDescription].filter(Boolean).join(': ') : undefined,
  }));

  const remaining = expectedStages.slice(stages.length);
  remaining.forEach((name, index) => {
    nodes.push({
      key: `expected-${stages.length + index}`,
      name,
      state: active ? (index === 0 && run.status === 'Running' ? 'running' : 'pending') : 'skipped',
    });
  });

  // Nothing to compare against (a pipeline's first run): show that something is still going on.
  if (remaining.length === 0 && run.status === 'Running') {
    nodes.push({ key: 'next', name: 'Next stage', state: 'running' });
  }

  return nodes;
}
