import { describe, expect, it } from 'vitest';
import type { Agent } from '../api/types';
import { allows, describeAssignment, eligibleAgents, isAnyAgent, knownTags } from './assignment';

const agent = (over: Partial<Agent>): Agent => ({
  id: 'a1',
  machineName: 'build-01',
  tags: [],
  assignedTags: [],
  version: '1',
  status: 'Online',
  lastHeartbeatAtUtc: '2026-01-01T00:00:00Z',
  info: null,
  uptimeSeconds: null,
  activeRuns: [],
  ...over,
});

const gpu = agent({ id: 'gpu', machineName: 'gpu-box', tags: ['linux', 'GPU'], assignedTags: ['gpu'] });
const plain = agent({ id: 'plain', machineName: 'plain', tags: ['linux'] });

describe('isAnyAgent', () => {
  it('is nothing assigned', () => {
    expect(isAnyAgent({ tags: [], agentIds: [] })).toBe(true);
    expect(isAnyAgent({ tags: ['gpu'], agentIds: [] })).toBe(false);
    expect(isAnyAgent({ tags: [], agentIds: ['a1'] })).toBe(false);
  });
});

describe('allows / eligibleAgents', () => {
  it('lets any agent run a pipeline nothing is assigned to', () => {
    expect(eligibleAgents({ tags: [], agentIds: [] }, [gpu, plain])).toEqual([gpu, plain]);
  });

  it('matches by any one of the tags, ignoring case', () => {
    expect(eligibleAgents({ tags: ['GPU', 'eu'], agentIds: [] }, [gpu, plain])).toEqual([gpu]);
    expect(allows({ tags: ['eu'], agentIds: [] }, plain)).toBe(false);
  });

  it('matches an agent by name too, and tags work alongside', () => {
    expect(eligibleAgents({ tags: ['gpu'], agentIds: ['plain'] }, [gpu, plain])).toEqual([gpu, plain]);
    expect(eligibleAgents({ tags: [], agentIds: ['plain'] }, [gpu, plain])).toEqual([plain]);
  });
});

describe('knownTags', () => {
  it('lists every tag of every agent once, lower case, sorted', () => {
    expect(knownTags([gpu, plain, agent({ tags: ['EU', 'linux'] })])).toEqual(['eu', 'gpu', 'linux']);
  });
});

describe('describeAssignment', () => {
  it('says any agent when nothing is assigned', () => {
    expect(describeAssignment({ tags: [], agentIds: [] }, [gpu])).toBe('any agent');
  });

  it('lists the tags and then the agents by name', () => {
    expect(describeAssignment({ tags: ['gpu', 'eu'], agentIds: ['plain'] }, [gpu, plain])).toBe('gpu, eu, plain');
  });

  it('does not fail on an agent it does not know', () => {
    expect(describeAssignment({ tags: [], agentIds: ['gone'] }, [gpu])).toBe('unknown agent');
  });
});
