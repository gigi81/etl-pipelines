import { Button, Group, Modal, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useClearHistory } from '../api/hooks';
import type { Pipeline } from '../api/types';
import { pipelineLabel } from '../lib/pipelineLabels';

/** Asks before deleting a pipeline's finished runs, which cannot be undone. A run in progress is left alone. */
export function ClearHistoryModal({ pipeline, opened, onClose }: { pipeline: Pipeline; opened: boolean; onClose: () => void }) {
  const clear = useClearHistory();

  const confirm = () =>
    clear.mutate(pipeline.id, {
      onSuccess: ({ deleted }) => {
        notifications.show({
          color: 'green',
          title: 'History cleared',
          message: deleted === 0 ? 'There were no finished runs to delete.' : `${deleted} finished ${deleted === 1 ? 'run was' : 'runs were'} deleted.`,
        });
        onClose();
      },
      onError: (error) => notifications.show({ color: 'red', title: 'Could not clear the history', message: error.message }),
    });

  return (
    <Modal opened={opened} onClose={onClose} title={`Clear the history of ${pipelineLabel(pipeline)}?`} centered>
      <Text size="sm">
        Every finished run of this pipeline is deleted, with its stages and resource charts. This cannot be undone. A run in progress is not
        affected.
      </Text>
      <Group justify="flex-end" mt="lg">
        <Button variant="default" onClick={onClose}>
          Cancel
        </Button>
        <Button color="red" onClick={confirm} loading={clear.isPending}>
          Delete the history
        </Button>
      </Group>
    </Modal>
  );
}
