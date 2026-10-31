import type { RunStatus } from '../api/types';

export const isActive = (status: RunStatus) =>
  status === 'Queued' || status === 'Dispatched' || status === 'Running';

export function formatDuration(milliseconds: number): string {
  if (!Number.isFinite(milliseconds) || milliseconds < 0) return '-';
  if (milliseconds < 1000) return `${Math.round(milliseconds)} ms`;
  const totalSeconds = Math.floor(milliseconds / 1000);
  const days = Math.floor(totalSeconds / 86400);
  const hours = Math.floor((totalSeconds % 86400) / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  if (days > 0) return `${days}d ${hours}h`;
  if (hours > 0) return `${hours}h ${minutes}m`;
  if (minutes > 0) return `${minutes}m ${seconds}s`;
  return `${seconds}s`;
}

export function formatBytes(bytes: number): string {
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${unit === 0 ? value : value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}

export const formatNumber = (value: number | null | undefined) =>
  value === null || value === undefined ? '-' : value.toLocaleString();

/** "5 minutes ago" / "in 2 hours" relative to `now`. */
export function formatRelative(iso: string, now: number = Date.now()): string {
  const seconds = Math.round((new Date(iso).getTime() - now) / 1000);
  const abs = Math.abs(seconds);
  const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
  if (abs < 60) return formatter.format(seconds, 'second');
  if (abs < 3600) return formatter.format(Math.round(seconds / 60), 'minute');
  if (abs < 86400) return formatter.format(Math.round(seconds / 3600), 'hour');
  return formatter.format(Math.round(seconds / 86400), 'day');
}

export const formatDateTime = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : '-');

/** How long a run has been going, or took: from start (or request) to completion (or `now`). */
export function runDurationMs(
  run: { requestedAtUtc: string; startedAtUtc: string | null; completedAtUtc: string | null },
  now: number = Date.now(),
): number {
  const start = new Date(run.startedAtUtc ?? run.requestedAtUtc).getTime();
  const end = run.completedAtUtc ? new Date(run.completedAtUtc).getTime() : now;
  return end - start;
}

/** 0-100 for a run that knows how many stages to expect; null when there is no basis for one. */
export function runProgress(run: { stagesCompleted: number; stagesExpected: number | null; status: RunStatus }): number | null {
  if (run.status === 'Succeeded') return 100;
  if (!run.stagesExpected) return null;
  return Math.min(99, Math.round((run.stagesCompleted / run.stagesExpected) * 100));
}
