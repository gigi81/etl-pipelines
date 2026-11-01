import { QueryClient } from '@tanstack/react-query';
import { describe, expect, it } from 'vitest';
import type { Agent, RunSummary } from '../api/types';
import { applyAgentEvent, applyAgentRemoved, applyRunEvent } from './liveCache';

const run = (id: string, over: Partial<RunSummary> = {}): RunSummary => ({
  id,
  pipelineId: 'p1',
  pipelineName: 'p',
  packageId: 'pkg',
  packageVersion: '1.0.0',
  agentId: null,
  agentName: null,
  status: 'Running',
  requestedAtUtc: '2026-01-01T00:00:00Z',
  startedAtUtc: null,
  completedAtUtc: null,
  exitCode: null,
  rowsRead: null,
  rowsWritten: null,
  rowsFailed: null,
  stagesCompleted: 0,
  stagesExpected: null,
  ...over,
});

const agent = (id: string, machineName: string): Agent => ({
  id,
  machineName,
  tags: [],
  version: '1',
  status: 'Online',
  lastHeartbeatAtUtc: '2026-01-01T00:00:00Z',
  info: null,
  uptimeSeconds: null,
  activeRuns: [],
});

/** A client with the queries a page would have mounted, and a record of what gets invalidated. */
function setup() {
  const client = new QueryClient();
  const invalidated: string[] = [];
  const original = client.invalidateQueries.bind(client);
  client.invalidateQueries = ((filters?: { queryKey?: readonly unknown[] }) => {
    invalidated.push(JSON.stringify(filters?.queryKey ?? 'all'));
    return original(filters);
  }) as typeof client.invalidateQueries;

  client.setQueryData(['runs', { active: true }], [run('a', { requestedAtUtc: '2026-01-01T00:00:01Z' })]);
  client.setQueryData(['runs', { active: false, limit: 100 }], []);
  client.setQueryData(['pipelines'], []);
  return { client, invalidated, active: () => client.getQueryData<RunSummary[]>(['runs', { active: true }])! };
}

describe('applyRunEvent', () => {
  it('adds a new run to the runs in flight, newest first, and refreshes the pipelines', () => {
    const { client, active, invalidated } = setup();

    applyRunEvent(client, run('b', { requestedAtUtc: '2026-01-01T00:00:05Z', status: 'Dispatched' }));

    expect(active().map((r) => r.id)).toEqual(['b', 'a']);
    expect(invalidated).toContain('["pipelines"]');
  });

  it('only updates a run that is making progress, and leaves the pipelines alone', () => {
    const { client, active, invalidated } = setup();

    applyRunEvent(client, run('a', { requestedAtUtc: '2026-01-01T00:00:01Z', stagesCompleted: 2 }));

    expect(active()).toHaveLength(1);
    expect(active()[0].stagesCompleted).toBe(2);
    expect(invalidated).toEqual([]);
  });

  it('drops a finished run from those in flight, and refreshes the history and the pipelines', () => {
    const { client, active, invalidated } = setup();

    applyRunEvent(client, run('a', { requestedAtUtc: '2026-01-01T00:00:01Z', status: 'Succeeded' }));

    expect(active()).toEqual([]);
    expect(invalidated).toContain('["runs",{"active":false,"limit":100}]');
    expect(invalidated).toContain('["pipelines"]');
  });
});

describe('applyAgentEvent', () => {
  it('adds an agent, replaces one that is already there, and keeps them by name', () => {
    const client = new QueryClient();
    client.setQueryData(['agents'], [agent('2', 'b'), agent('1', 'a')]);

    applyAgentEvent(client, { ...agent('2', 'b'), status: 'Offline' });
    applyAgentEvent(client, agent('3', 'aa'));

    const agents = client.getQueryData<Agent[]>(['agents'])!;
    expect(agents.map((a) => a.machineName)).toEqual(['a', 'aa', 'b']);
    expect(agents.find((a) => a.id === '2')!.status).toBe('Offline');
  });

  it('does nothing when the agents have not been fetched yet (they will be, whole)', () => {
    const client = new QueryClient();

    applyAgentEvent(client, agent('1', 'a'));

    expect(client.getQueryData(['agents'])).toBeUndefined();
  });
});

describe('applyAgentRemoved', () => {
  it('drops the agent from the list and leaves the others', () => {
    const client = new QueryClient();
    client.setQueryData(['agents'], [agent('1', 'a'), agent('2', 'b')]);

    applyAgentRemoved(client, '1');

    expect(client.getQueryData<Agent[]>(['agents'])!.map((a) => a.id)).toEqual(['2']);
  });

  it('does nothing when the agents have not been fetched yet', () => {
    const client = new QueryClient();

    applyAgentRemoved(client, '1');

    expect(client.getQueryData(['agents'])).toBeUndefined();
  });
});
