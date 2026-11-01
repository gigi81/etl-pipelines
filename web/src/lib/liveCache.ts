import type { QueryClient } from '@tanstack/react-query';
import type { Agent, RunSummary } from '../api/types';
import { isActive } from './format';

/** The parameters a `['runs', params]` query was made with (see `useRuns`). */
interface RunsParams {
  active?: boolean;
  pipelineId?: string;
  limit?: number;
}

const newestFirst = (a: RunSummary, b: RunSummary) => b.requestedAtUtc.localeCompare(a.requestedAtUtc);

/**
 * Applies a `run` event from the server to what is already on screen, so the lists change without
 * being fetched again:
 * - the lists of runs in flight gain, update or lose the run;
 * - the lists of finished runs are refetched when a run finishes (they hold history the event does
 *   not carry);
 * - the pipelines (each shows its running count and last run) are refetched when the run's status
 *   changed, but not for mere progress, which is most events.
 */
export function applyRunEvent(queryClient: QueryClient, run: RunSummary): void {
  const queries = queryClient.getQueryCache().findAll({ queryKey: ['runs'] });
  let previous: RunSummary | undefined;

  for (const query of queries) {
    const params = (query.queryKey[1] ?? {}) as RunsParams;

    if (params.active === true) {
      previous ??= (query.state.data as RunSummary[] | undefined)?.find((r) => r.id === run.id);

      queryClient.setQueryData<RunSummary[]>(query.queryKey, (held) => {
        if (!held) return held;
        const others = held.filter((r) => r.id !== run.id);
        if (!isActive(run.status) || (params.pipelineId && params.pipelineId !== run.pipelineId)) return others;
        return [...others, run].sort(newestFirst);
      });
    } else if (!isActive(run.status)) {
      void queryClient.invalidateQueries({ queryKey: query.queryKey });
    }
  }

  if (!previous || previous.status !== run.status) {
    void queryClient.invalidateQueries({ queryKey: ['pipelines'] });
  }
}

/** Applies an `agent-removed` event: the agent is no longer listed. */
export function applyAgentRemoved(queryClient: QueryClient, agentId: string): void {
  queryClient.setQueryData<Agent[]>(['agents'], (held) => held?.filter((a) => a.id !== agentId));
}

/** Applies an `agent` event: the agent is added to the agents on screen, or replaced there. */
export function applyAgentEvent(queryClient: QueryClient, agent: Agent): void {
  queryClient.setQueryData<Agent[]>(['agents'], (held) => {
    if (!held) return held;
    const others = held.filter((a) => a.id !== agent.id);
    return [...others, agent].sort((a, b) => a.machineName.localeCompare(b.machineName));
  });
}
