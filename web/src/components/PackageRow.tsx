import { Badge, Button, Group, Loader, Select, Stack, Table, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconArrowUp, IconDownload, IconTrash } from '@tabler/icons-react';
import { useState } from 'react';
import { useInstallPackage, useUpdatePackage } from '../api/hooks';
import type { PackageInfo } from '../api/types';
import { packageState, versionOptions } from '../lib/packageVersions';
import { UninstallPackageModal } from './UninstallPackageModal';

/**
 * One package: what is installed, what the feed has, and what can be done about it. Installing and
 * updating are done by an agent (`dotnet tool install`), so they can take minutes: the row shows it is
 * working and the buttons wait.
 */
export function PackageRow({ pkg }: { pkg: PackageInfo }) {
  const install = useInstallPackage();
  const update = useUpdatePackage();
  const [version, setVersion] = useState('');
  const [confirmingUninstall, setConfirmingUninstall] = useState(false);

  const state = packageState(pkg);
  const busy = install.isPending || update.isPending;

  const failed = (title: string) => (error: Error) =>
    notifications.show({ color: 'red', title, message: error.message, autoClose: 10000 });

  const installPackage = () =>
    install.mutate(
      { id: pkg.id, version: version || undefined },
      {
        onSuccess: (result) =>
          notifications.show({
            color: 'green',
            title: `Installed ${result.packageId} ${result.version}`,
            message: result.pipelineNames.length
              ? `Pipelines: ${result.pipelineNames.join(', ')}`
              : 'The package registers no pipelines.',
          }),
        onError: failed(`Could not install ${pkg.id}`),
      },
    );

  const updatePackage = () =>
    update.mutate(pkg.id, {
      onSuccess: (result) =>
        notifications.show({ color: 'green', title: `Updated ${result.packageId}`, message: `Now at ${result.version}.` }),
      onError: failed(`Could not update ${pkg.id}`),
    });

  return (
    <>
      <Table.Tr>
        <Table.Td>
          <Text fw={500}>{pkg.id}</Text>
          {pkg.pipelines.length > 0 && (
            <Group gap={4} mt={4}>
              {pkg.pipelines.map((name) => (
                <Badge key={name} size="sm" variant="outline" color="gray">
                  {name}
                </Badge>
              ))}
            </Group>
          )}
        </Table.Td>

        <Table.Td>
          {pkg.installedVersion ? (
            <Stack gap={2}>
              <Badge color="green" variant="light" w="fit-content">
                {pkg.installedVersion}
              </Badge>
              {state === 'update-available' && (
                <Text size="xs" c="orange">
                  {pkg.latestStableVersion} available
                </Text>
              )}
              {state === 'not-on-feed' && (
                <Text size="xs" c="dimmed">
                  no longer on the feed
                </Text>
              )}
            </Stack>
          ) : (
            <Text c="dimmed" size="sm">
              Not installed
            </Text>
          )}
        </Table.Td>

        <Table.Td>
          <Text size="sm" c={pkg.latestStableVersion ? undefined : 'dimmed'}>
            {pkg.latestStableVersion ?? (pkg.versions[0] ? `${pkg.versions[0]} (prerelease)` : '-')}
          </Text>
        </Table.Td>

        <Table.Td>
          <Group gap="xs" justify="flex-end" wrap="nowrap">
            {busy && (
              <Group gap={6} wrap="nowrap" mr="xs">
                <Loader size="xs" />
                <Text size="xs" c="dimmed">
                  An agent is installing it - this can take a few minutes
                </Text>
              </Group>
            )}

            {state === 'not-installed' && (
              <>
                <Select
                  aria-label={`Version of ${pkg.id} to install`}
                  data={versionOptions(pkg)}
                  value={version}
                  onChange={(value) => setVersion(value ?? '')}
                  allowDeselect={false}
                  size="xs"
                  w={190}
                  disabled={busy}
                />
                <Button size="xs" leftSection={<IconDownload size={14} />} onClick={installPackage} loading={install.isPending} disabled={busy}>
                  Install
                </Button>
              </>
            )}

            {state === 'update-available' && (
              <Button
                size="xs"
                color="orange"
                variant="light"
                leftSection={<IconArrowUp size={14} />}
                onClick={updatePackage}
                loading={update.isPending}
                disabled={busy}
              >
                Update to {pkg.latestStableVersion}
              </Button>
            )}

            {pkg.installedVersion && (
              <Button
                size="xs"
                color="red"
                variant="subtle"
                leftSection={<IconTrash size={14} />}
                onClick={() => setConfirmingUninstall(true)}
                disabled={busy}
              >
                Uninstall
              </Button>
            )}
          </Group>
        </Table.Td>
      </Table.Tr>

      <UninstallPackageModal pkg={pkg} opened={confirmingUninstall} onClose={() => setConfirmingUninstall(false)} />
    </>
  );
}
