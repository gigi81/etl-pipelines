import { Alert, Button, Group, Loader, SimpleGrid, Stack, Text, Title } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { IconAlertCircle, IconTrash } from '@tabler/icons-react';
import { useAgents } from '../api/hooks';
import { AgentCard } from '../components/AgentCard';
import { RemoveAgentsModal } from '../components/RemoveAgentsModal';

export function AgentsPage() {
  const { data: agents, error, isLoading } = useAgents();
  const [removing, { open, close }] = useDisclosure();
  const offline = agents?.filter((agent) => agent.status !== 'Online') ?? [];

  return (
    <Stack>
      <Group justify="space-between">
        <Title order={2}>Agents</Title>
        {offline.length > 0 && (
          <Button variant="default" color="red" leftSection={<IconTrash size={16} />} onClick={open}>
            Remove offline agents ({offline.length})
          </Button>
        )}
      </Group>
      {isLoading && <Loader />}
      {error && (
        <Alert color="red" icon={<IconAlertCircle />} title="Could not load the agents">
          {error.message}
        </Alert>
      )}
      {agents?.length === 0 && <Text c="dimmed">No agent has registered with this server yet.</Text>}
      <SimpleGrid cols={{ base: 1, md: 2, xl: 3 }}>
        {agents?.map((agent) => <AgentCard key={agent.id} agent={agent} />)}
      </SimpleGrid>
      <RemoveAgentsModal agents={offline} opened={removing} onClose={close} />
    </Stack>
  );
}
