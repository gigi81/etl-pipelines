import type { AgentInfo } from '../api/types';

/** Keys of `AgentInfo` the card has its own row for; any other key is listed after them. */
const KNOWN_INFO_KEYS = new Set(['osDescription', 'osArchitecture', 'processorCount', 'ipAddresses', 'startedAt']);

/** "someNewDetail" -> "Some new detail". */
const humanize = (key: string) => {
  const spaced = key.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
};

const formatInfoValue = (value: unknown): string =>
  Array.isArray(value) ? value.map(formatInfoValue).join(', ') : typeof value === 'object' && value !== null ? JSON.stringify(value) : String(value);

/** The details an agent reported that this page has no dedicated row for - so a new one shows up with no UI change. */
export const extraInfo = (info: AgentInfo | null): [string, string][] =>
  Object.entries(info ?? {})
    .filter(([key, value]) => !KNOWN_INFO_KEYS.has(key) && value !== null && value !== undefined && value !== '')
    .map(([key, value]) => [humanize(key), formatInfoValue(value)]);
