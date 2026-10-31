import { Alert, Loader, SimpleGrid, Stack, Text, Title } from '@mantine/core';
import { IconAlertCircle } from '@tabler/icons-react';
import { useAgents } from '../api/hooks';
import { AgentCard } from '../components/AgentCard';

export function AgentsPage() {
  const { data: agents, error, isLoading } = useAgents();

  return (
    <Stack>
      <Title order={2}>Agents</Title>
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
    </Stack>
  );
}
