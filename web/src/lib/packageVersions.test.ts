import { describe, expect, it } from 'vitest';
import type { PackageInfo } from '../api/types';
import { isPrerelease, packageState, versionOptions } from './packageVersions';

const pkg = (over: Partial<PackageInfo> = {}): PackageInfo => ({
  id: 'Contoso.Orders',
  versions: ['3.0.0-beta.1', '2.0.0', '1.0.0'],
  latestStableVersion: '2.0.0',
  installedVersion: null,
  updateAvailable: false,
  pipelines: [],
  ...over,
});

describe('versionOptions', () => {
  it('offers the latest stable version first, as the default, then every version on the feed', () => {
    const options = versionOptions(pkg());

    expect(options[0]).toEqual({ value: '', label: 'Latest stable (2.0.0)' });
    expect(options.slice(1).map((o) => o.value)).toEqual(['3.0.0-beta.1', '2.0.0', '1.0.0']);
  });

  it('still offers the default when the feed has nothing stable', () => {
    expect(versionOptions(pkg({ versions: ['1.0.0-rc.1'], latestStableVersion: null }))[0].label).toBe('Latest stable');
  });
});

describe('packageState', () => {
  it('says what can be done', () => {
    expect(packageState(pkg())).toBe('not-installed');
    expect(packageState(pkg({ installedVersion: '1.0.0', updateAvailable: true }))).toBe('update-available');
    expect(packageState(pkg({ installedVersion: '2.0.0' }))).toBe('up-to-date');
    expect(packageState(pkg({ installedVersion: '1.0.0', versions: [], latestStableVersion: null }))).toBe('not-on-feed');
  });
});

describe('isPrerelease', () => {
  it('recognises a label', () => {
    expect(isPrerelease('3.0.0-beta.1')).toBe(true);
    expect(isPrerelease('3.0.0')).toBe(false);
  });
});
