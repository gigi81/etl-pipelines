import type {
  Agent,
  ClearedHistory,
  ConfigurationKey,
  InstallResult,
  PackagesResponse,
  Pipeline,
  PipelineAssignment,
  RunDetail,
  RunSummary,
  ServerInfo,
  UploadedPackage,
} from './types';

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
  // 204 No Content: nothing to read.
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

export const api = {
  info: () => request<ServerInfo>('/api/info'),
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
  /** Replaces the tags a user gave the agent; the ones it reports about itself are not affected. */
  setAgentTags: (id: string, tags: string[]) =>
    request<void>(`/api/agents/${id}/tags`, {
      method: 'PUT',
      body: JSON.stringify({ tags }),
      headers: { 'Content-Type': 'application/json' },
    }),
  /** Which agents may run the pipeline; both lists empty means any agent. */
  setAssignment: (pipelineId: string, assignment: PipelineAssignment) =>
    request<void>(`/api/pipelines/${pipelineId}/assignment`, {
      method: 'PUT',
      body: JSON.stringify(assignment),
      headers: { 'Content-Type': 'application/json' },
    }),
  removeAgent: (id: string) => request<void>(`/api/agents/${id}`, { method: 'DELETE' }),
  /** An empty name goes back to the pipeline's own. */
  setDisplayName: (pipelineId: string, displayName: string) =>
    request<void>(`/api/pipelines/${pipelineId}/display-name`, {
      method: 'PUT',
      body: JSON.stringify({ displayName }),
      headers: { 'Content-Type': 'application/json' },
    }),
  clearHistory: (pipelineId: string) => request<ClearedHistory>(`/api/pipelines/${pipelineId}/runs`, { method: 'DELETE' }),
  packages: (search?: string) =>
    request<PackagesResponse>(`/api/packages${search ? `?search=${encodeURIComponent(search)}` : ''}`),
  /** Installs on an agent, so it takes as long as the agent does (minutes, for a first install). */
  installPackage: (id: string, version?: string) =>
    request<InstallResult>(
      `/api/packages/${encodeURIComponent(id)}/install${version ? `?version=${encodeURIComponent(version)}` : ''}`,
      { method: 'POST' },
    ),
  updatePackage: (id: string) =>
    request<InstallResult>(`/api/packages/${encodeURIComponent(id)}/update`, { method: 'POST' }),
  uninstallPackage: (id: string) => request<void>(`/api/packages/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  /** The .nupkg is the raw request body. */
  uploadPackage: (file: File) =>
    request<UploadedPackage>('/api/packages/upload', {
      method: 'POST',
      body: file,
      headers: { 'Content-Type': 'application/octet-stream' },
    }),
  configurationKeys: () => request<ConfigurationKey[]>('/api/configuration'),
  setConfiguration: (key: string, value: string) =>
    request<void>('/api/configuration', {
      method: 'PUT',
      body: JSON.stringify({ key, value }),
      headers: { 'Content-Type': 'application/json' },
    }),
  deleteConfiguration: (key: string) =>
    request<void>(`/api/configuration?key=${encodeURIComponent(key)}`, { method: 'DELETE' }),
};

/** The URL of the server-sent-events stream that follows one run. */
export const runStreamUrl = (runId: string) => `/api/runs/${runId}/stream`;
