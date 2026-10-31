import { describe, expect, it } from 'vitest';
import { formatBytes, formatDuration, runDurationMs, runProgress } from './format';

describe('formatDuration', () => {
  it('formats milliseconds, seconds, minutes, hours and days', () => {
    expect(formatDuration(450)).toBe('450 ms');
    expect(formatDuration(42_000)).toBe('42s');
    expect(formatDuration(125_000)).toBe('2m 5s');
    expect(formatDuration(3 * 3600_000 + 5 * 60_000)).toBe('3h 5m');
    expect(formatDuration(2 * 86400_000 + 4 * 3600_000)).toBe('2d 4h');
  });

  it('shows a dash for nonsense', () => {
    expect(formatDuration(-1)).toBe('-');
    expect(formatDuration(Number.NaN)).toBe('-');
  });
});

describe('formatBytes', () => {
  it('uses binary units', () => {
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(1536)).toBe('1.5 KiB');
    expect(formatBytes(123_456_789)).toBe('118 MiB');
  });
});

describe('runDurationMs', () => {
  it('runs from start to completion, or to now while running', () => {
    const run = { requestedAt: '2026-01-01T00:00:00Z', startedAt: '2026-01-01T00:00:10Z', completedAt: null };
    expect(runDurationMs(run, Date.parse('2026-01-01T00:01:10Z'))).toBe(60_000);
    expect(runDurationMs({ ...run, completedAt: '2026-01-01T00:00:40Z' })).toBe(30_000);
  });

  it('falls back to the request time for a run that has not started', () => {
    const run = { requestedAt: '2026-01-01T00:00:00Z', startedAt: null, completedAt: null };
    expect(runDurationMs(run, Date.parse('2026-01-01T00:00:05Z'))).toBe(5_000);
  });
});

describe('runProgress', () => {
  it('is a percentage of the expected stages, capped below 100 until it succeeds', () => {
    expect(runProgress({ stagesCompleted: 1, stagesExpected: 4, status: 'Running' })).toBe(25);
    expect(runProgress({ stagesCompleted: 4, stagesExpected: 4, status: 'Running' })).toBe(99);
    expect(runProgress({ stagesCompleted: 4, stagesExpected: 4, status: 'Succeeded' })).toBe(100);
  });

  it('is unknown when no expectation exists', () => {
    expect(runProgress({ stagesCompleted: 2, stagesExpected: null, status: 'Running' })).toBeNull();
  });
});
