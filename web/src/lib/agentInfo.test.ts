import { describe, expect, it } from 'vitest';
import { extraInfo } from './agentInfo';

describe('extraInfo', () => {
  it('lists only the details the agents page has no row of its own for', () => {
    const info = {
      osDescription: 'Ubuntu',
      ipAddresses: ['10.0.0.7', '2001:db8::7'],
      containerRuntime: 'docker',
      gpuCount: 2,
      labels: ['a', 'b'],
      empty: '',
    };

    expect(extraInfo(info)).toEqual([
      ['Container runtime', 'docker'],
      ['Gpu count', '2'],
      ['Labels', 'a, b'],
    ]);
  });

  it('is empty for an agent that reported nothing', () => {
    expect(extraInfo(null)).toEqual([]);
  });
});
