import { Button, Group, Modal, Stack, Text, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useEffect, useState } from 'react';
import { useSetDisplayName } from '../api/hooks';
import type { Pipeline } from '../api/types';

const MAX_LENGTH = 100;

/**
 * Gives a pipeline a display name. Its own name - what its package registers, and what an agent
 * launches it by - cannot change, so this only changes how it is labelled here; an empty name goes
 * back to the pipeline's own.
 */
export function RenamePipelineModal({ pipeline, opened, onClose }: { pipeline: Pipeline; opened: boolean; onClose: () => void }) {
  const save = useSetDisplayName();
  const [name, setName] = useState(pipeline.displayName ?? '');

  // Start from what the pipeline is called now each time the dialog is opened.
  useEffect(() => {
    if (opened) setName(pipeline.displayName ?? '');
  }, [opened, pipeline.displayName]);

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    save.mutate(
      { pipelineId: pipeline.id, displayName: name.trim() },
      {
        onSuccess: () => {
          notifications.show({
            color: 'green',
            title: name.trim() ? 'Renamed' : 'Name reset',
            message: name.trim() ? `${pipeline.name} is now shown as "${name.trim()}".` : `${pipeline.name} is shown by its own name again.`,
          });
          onClose();
        },
        onError: (error) => notifications.show({ color: 'red', title: 'Could not rename', message: error.message }),
      },
    );
  };

  return (
    <Modal opened={opened} onClose={onClose} title={`Rename ${pipeline.displayName ?? pipeline.name}`} centered>
      <form onSubmit={submit}>
        <Stack>
          <TextInput
            label="Display name"
            placeholder={pipeline.name}
            value={name}
            onChange={(event) => setName(event.currentTarget.value)}
            maxLength={MAX_LENGTH}
            data-autofocus
          />
          <Text size="xs" c="dimmed">
            The pipeline's own name, <code>{pipeline.name}</code>, is set by its package and is what runs it, so it stays. Leave this empty
            to show it as it is.
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={save.isPending} disabled={name.trim() === (pipeline.displayName ?? '')}>
              Save
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
