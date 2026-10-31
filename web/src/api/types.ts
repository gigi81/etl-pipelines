// Every timestamp is ISO 8601 in UTC (ending in Z) and its name ends in `Utc`.
// Mirrors the records in src/EtlPipelines.Server.Contracts/ (camelCase on the wire).

export type RunStatus = 'Queued' | 'Dispatched' | 'Running' | 'Succeeded' | 'Failed' | 'AgentLost';

export interface RunSummary {
  id: string;
  pipelineId: string;
  pipelineName: string;
  packageId: string;
  packageVersion: string;
  agentId: string | null;
  agentName: string | null;
  status: RunStatus;
  requestedAtUtc: string;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  exitCode: number | null;
  rowsRead: number | null;
  rowsWritten: number | null;
  rowsFailed: number | null;
  stagesCompleted: number;
  stagesExpected: number | null;
}

export interface Pipeline {
  id: string;
  name: string;
  packageId: string;
  packageVersion: string;
  activeRuns: number;
  lastRun: RunSummary | null;
}

export interface Stage {
  sequence: number;
  name: string;
  rowsIn: number;
  rowsOut: number;
  rowsFailed: number;
  elapsedMs: number;
  errorCode: string | null;
  errorDescription: string | null;
}

export interface ResourceSample {
  sampledAtUtc: string;
  cpuPercent: number;
  workingSetBytes: number;
}

export interface RunDetail {
  run: RunSummary;
  stages: Stage[];
  expectedStages: string[];
  resourceSamples: ResourceSample[];
}

/**
 * What an agent reported about its host (Agents.Info): a free-form JSON object, so it can grow
 * without a schema change. The keys below are the ones the agent sends today; anything else is
 * passed through and shown by the agents page as it is.
 */
export interface AgentInfo {
  osDescription?: string;
  osArchitecture?: string;
  processorCount?: number;
  /** Every address of the host, IPv4 and IPv6 alike. */
  ipAddresses?: string[];
  /** ISO 8601, UTC - when the agent process started. */
  startedAtUtc?: string;
  [key: string]: unknown;
}

export interface Agent {
  id: string;
  machineName: string;
  tags: string[];
  version: string;
  status: 'Online' | 'Offline';
  lastHeartbeatAtUtc: string;
  info: AgentInfo | null;
  uptimeSeconds: number | null;
  activeRuns: RunSummary[];
}
