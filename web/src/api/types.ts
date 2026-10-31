// Mirrors the records in src/EtlPipelines.Server/Api/ApiModels.cs (camelCase on the wire).

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
  requestedAt: string;
  startedAt: string | null;
  completedAt: string | null;
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
  sampledAt: string;
  cpuPercent: number;
  workingSetBytes: number;
}

export interface RunDetail {
  run: RunSummary;
  stages: Stage[];
  expectedStages: string[];
  resourceSamples: ResourceSample[];
}

export interface Agent {
  id: string;
  machineName: string;
  tags: string[];
  version: string;
  status: 'Online' | 'Offline';
  lastHeartbeatAt: string;
  osDescription: string | null;
  osArchitecture: string | null;
  ipAddresses: string[];
  processorCount: number | null;
  startedAt: string | null;
  uptimeSeconds: number | null;
  activeRuns: RunSummary[];
}
