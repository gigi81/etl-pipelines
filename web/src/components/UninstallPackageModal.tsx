import { Button, Group, Modal, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useUninstallPackage } from '../api/hooks';
import type { PackageInfo } from '../api/types';

/** Asks before removing a package: its pipelines stop being listed and can no longer be run. */
export function UninstallPackageModal({ pkg, opened, onClose }: { pkg: PackageInfo; opened: boolean; onClose: () => void }) {
  const uninstall = useUninstallPackage();

  const confirm = () =>
    uninstall.mutate(pkg.id, {
      onSuccess: () => {
        notifications.show({ color: 'green', title: `Uninstalled ${pkg.id}`, message: 'Its pipelines are no longer listed.' });
        onClose();
      },
      onError: (error) =>
        notifications.show({ color: 'red', title: `Could not uninstall ${pkg.id}`, message: error.message }),
    });

  return (
    <Modal opened={opened} onClose={onClose} title={`Uninstall ${pkg.id}?`} centered>
      <Text size="sm">
        {pkg.pipelines.length > 0
          ? `Its ${pkg.pipelines.length === 1 ? 'pipeline' : 'pipelines'} (${pkg.pipelines.join(', ')}) will no longer be listed or run. `
          : 'Nothing will be listed or run from it. '}
        Runs already in progress finish. You can install it again later.
      </Text>
      <Group justify="flex-end" mt="lg">
        <Button variant="default" onClick={onClose}>
          Cancel
        </Button>
        <Button color="red" onClick={confirm} loading={uninstall.isPending}>
          Uninstall
        </Button>
      </Group>
    </Modal>
  );
}
