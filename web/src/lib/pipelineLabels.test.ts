import { describe, expect, it } from 'vitest';
import type { Pipeline } from '../api/types';
import { buildPipelineLabeller, pipelineLabel } from './pipelineLabels';

const pipeline = (over: Partial<Pipeline>): Pipeline => ({
  id: '1',
  name: 'orders-load',
  displayName: null,
  packageId: 'Contoso.Orders',
  packageVersion: '1.0.0',
  activeRuns: 0,
  lastRun: null,
  assignment: { tags: [], agentIds: [] },
  ...over,
});

describe('buildPipelineLabeller', () => {
  it('labels a run by the display name of its pipeline, found by package and own name', () => {
    const label = buildPipelineLabeller([pipeline({ displayName: 'Nightly orders' }), pipeline({ id: '2', name: 'orders-archive' })]);

    expect(label('Contoso.Orders', 'orders-load')).toBe('Nightly orders');
    expect(label('Contoso.Orders', 'orders-archive')).toBe('orders-archive');
  });

  it('does not mix up the same pipeline name in another package', () => {
    const label = buildPipelineLabeller([pipeline({ displayName: 'Nightly orders' })]);

    expect(label('Fabrikam.Other', 'orders-load')).toBe('orders-load');
  });

  it('falls back to the own name until the pipelines are loaded', () => {
    expect(buildPipelineLabeller(undefined)('Contoso.Orders', 'orders-load')).toBe('orders-load');
  });
});

describe('pipelineLabel', () => {
  it('prefers the display name', () => {
    expect(pipelineLabel(pipeline({ displayName: 'Nightly orders' }))).toBe('Nightly orders');
    expect(pipelineLabel(pipeline({}))).toBe('orders-load');
  });
});
