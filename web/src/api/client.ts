import type { Agent, Pipeline, RunDetail, RunSummary } from './types';

/** A non-2xx answer from the server; `detail` is the ProblemDetails text when there is one. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, { ...init, headers: { Accept: 'application/json', ...init?.headers } });
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try {
      const problem = (await response.json()) as { detail?: string; title?: string };
      message = problem.detail ?? problem.title ?? message;
    } catch {
      // Not a ProblemDetails body - the status line has to do.
    }
    throw new ApiError(response.status, message);
  }
  return (await response.json()) as T;
}

export const api = {
  pipelines: () => request<Pipeline[]>('/api/pipelines'),
  triggerRun: (pipelineId: string) =>
    request<{ runId: string }>(`/api/pipelines/${pipelineId}/runs`, { method: 'POST' }),
  runs: (query: { active?: boolean; pipelineId?: string; limit?: number } = {}) => {
    const params = new URLSearchParams();
    if (query.active !== undefined) params.set('active', String(query.active));
    if (query.pipelineId) params.set('pipelineId', query.pipelineId);
    if (query.limit) params.set('limit', String(query.limit));
    return request<RunSummary[]>(`/api/runs?${params}`);
  },
  run: (runId: string) => request<RunDetail>(`/api/runs/${runId}`),
  agents: () => request<Agent[]>('/api/agents'),
};

/** The URL of the server-sent-events stream that follows one run. */
export const runStreamUrl = (runId: string) => `/api/runs/${runId}/stream`;
