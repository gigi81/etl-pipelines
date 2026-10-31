import { Alert, Badge, Button, Card, Group, Loader, SimpleGrid, Stack, Text, Title } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconAlertCircle, IconPlayerPlay } from '@tabler/icons-react';
import { Link, useNavigate } from 'react-router-dom';
import { useNow, usePipelines, useTriggerRun } from '../api/hooks';
import { StatusBadge } from '../components/StatusBadge';
import { formatRelative } from '../lib/format';

export function PipelinesPage() {
  const { data: pipelines, error, isLoading } = usePipelines();
  const trigger = useTriggerRun();
  const navigate = useNavigate();
  const now = useNow(5000);

  const run = (pipelineId: string, name: string) =>
    trigger.mutate(pipelineId, {
      onSuccess: ({ runId }) => {
        notifications.show({ color: 'green', title: 'Run started', message: `${name} was sent to an agent.` });
        navigate(`/runs/${runId}`);
      },
      onError: (failure) =>
        notifications.show({ color: 'red', title: `Could not run ${name}`, message: failure.message }),
    });

  return (
    <Stack>
      <Title order={2}>Pipelines</Title>
      {isLoading && <Loader />}
      {error && (
        <Alert color="red" icon={<IconAlertCircle />} title="Could not load the pipelines">
          {error.message}
        </Alert>
      )}
      {pipelines?.length === 0 && (
        <Text c="dimmed">No pipelines are installed yet. Install a package from the feed to see its pipelines here.</Text>
      )}
      <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }}>
        {pipelines?.map((pipeline) => (
          <Card key={pipeline.id} withBorder padding="lg" radius="md">
            <Stack gap="xs" h="100%" justify="space-between">
              <div>
                <Group justify="space-between" wrap="nowrap" align="flex-start">
                  <Text fw={600} size="lg" truncate>
                    {pipeline.name}
                  </Text>
                  {pipeline.activeRuns > 0 && (
                    <Badge color="blue" variant="filled" component={Link} to="/runs" style={{ cursor: 'pointer' }}>
                      {pipeline.activeRuns} running
                    </Badge>
                  )}
                </Group>
                <Text size="sm" c="dimmed">
                  {pipeline.packageId} {pipeline.packageVersion}
                </Text>
              </div>

              <Group gap="xs" mih={24}>
                {pipeline.lastRun ? (
                  <>
                    <StatusBadge status={pipeline.lastRun.status} />
                    <Text size="xs" c="dimmed" component={Link} to={`/runs/${pipeline.lastRun.id}`}>
                      last run {formatRelative(pipeline.lastRun.requestedAtUtc, now)}
                    </Text>
                  </>
                ) : (
                  <Text size="xs" c="dimmed">
                    Never run
                  </Text>
                )}
              </Group>

              <Button
                leftSection={<IconPlayerPlay size={16} />}
                onClick={() => run(pipeline.id, pipeline.name)}
                loading={trigger.isPending && trigger.variables === pipeline.id}
              >
                Run now
              </Button>
            </Stack>
          </Card>
        ))}
      </SimpleGrid>
    </Stack>
  );
}
