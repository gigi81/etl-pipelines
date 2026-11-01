import { Button, Group, List, Modal, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useState } from 'react';
import { useRemoveAgent } from '../api/hooks';
import type { Agent } from '../api/types';

/**
 * Asks before removing agents that have stopped heartbeating - the registrations a restarted or
 * retired agent leaves behind. They are only hidden from the list: the runs they carried out keep
 * their name. An agent that comes back has to be restarted to register again.
 */
export function RemoveAgentsModal({ agents, opened, onClose }: { agents: Agent[]; opened: boolean; onClose: () => void }) {
  const remove = useRemoveAgent();
  const [working, setWorking] = useState(false);

  const confirm = async () => {
    setWorking(true);
    const failures: string[] = [];
    for (const agent of agents) {
      try {
        await remove.mutateAsync(agent.id);
      } catch (error) {
        failures.push(`${agent.machineName}: ${(error as Error).message}`);
      }
    }

    setWorking(false);
    const removed = agents.length - failures.length;
    if (removed > 0) {
      notifications.show({ color: 'green', title: 'Removed', message: `${removed} ${removed === 1 ? 'agent was' : 'agents were'} removed.` });
    }

    if (failures.length > 0) {
      notifications.show({ color: 'red', title: 'Could not remove everything', message: failures.join('\n'), autoClose: 10000 });
    }

    onClose();
  };

  return (
    <Modal opened={opened} onClose={onClose} title={agents.length === 1 ? `Remove ${agents[0]?.machineName}?` : `Remove ${agents.length} offline agents?`} centered>
      {agents.length > 1 && (
        <List size="sm" mb="sm" spacing={2}>
          {agents.map((agent) => (
            <List.Item key={agent.id}>{agent.machineName}</List.Item>
          ))}
        </List>
      )}
      <Text size="sm">
        {agents.length === 1 ? 'It has' : 'They have'} stopped responding, and will no longer be listed. The runs {agents.length === 1 ? 'it' : 'they'} carried
        out stay in the history. An agent that is still alive and comes back has to be restarted to register again.
      </Text>
      <Group justify="flex-end" mt="lg">
        <Button variant="default" onClick={onClose} disabled={working}>
          Cancel
        </Button>
        <Button color="red" onClick={() => void confirm()} loading={working}>
          Remove
        </Button>
      </Group>
    </Modal>
  );
}
