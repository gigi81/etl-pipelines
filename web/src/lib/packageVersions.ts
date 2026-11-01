import type { PackageInfo } from '../api/types';

export interface VersionOption {
  /** Empty means "let the server pick the latest stable version". */
  value: string;
  label: string;
}

/** The versions a package can be installed at: the latest stable first (the default), then each one the feed has. */
export function versionOptions(pkg: PackageInfo): VersionOption[] {
  const latest: VersionOption = {
    value: '',
    label: pkg.latestStableVersion ? `Latest stable (${pkg.latestStableVersion})` : 'Latest stable',
  };

  return [latest, ...pkg.versions.map((version) => ({ value: version, label: version }))];
}

/** Whether a version is a prerelease (it has a `-label`). */
export const isPrerelease = (version: string) => version.includes('-');

/** What can be done with a package right now. */
export type PackageState = 'not-installed' | 'update-available' | 'up-to-date' | 'not-on-feed';

export function packageState(pkg: PackageInfo): PackageState {
  if (pkg.installedVersion === null) return 'not-installed';
  if (pkg.updateAvailable) return 'update-available';
  return pkg.versions.length === 0 ? 'not-on-feed' : 'up-to-date';
}
