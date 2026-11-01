import { Alert, Badge, Button, Card, Group, Loader, SimpleGrid, Stack, Text, Title } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { IconAlertCircle, IconPackage, IconPlayerPlay, IconUpload } from '@tabler/icons-react';
import { Link, useNavigate } from 'react-router-dom';
import { useNow, usePipelines, useTriggerRun } from '../api/hooks';
import { StatusBadge } from '../components/StatusBadge';
import { UploadPackageModal } from '../components/UploadPackageModal';
import { formatRelative } from '../lib/format';

export function PipelinesPage() {
  const { data: pipelines, error, isLoading } = usePipelines();
  const trigger = useTriggerRun();
  const navigate = useNavigate();
  const now = useNow(5000);
  const [uploading, { open, close }] = useDisclosure();

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
      <Group justify="space-between">
        <Title order={2}>Pipelines</Title>
        {/* Always here, not only when the list is empty: a pipeline comes from a package, and more can always be added. */}
        <Group gap="xs">
          <Button variant="default" leftSection={<IconUpload size={16} />} onClick={open}>
            Upload a package
          </Button>
          <Button component={Link} to="/packages" leftSection={<IconPackage size={16} />}>
            Add pipelines
          </Button>
        </Group>
      </Group>
      {isLoading && <Loader />}
      {error && (
        <Alert color="red" icon={<IconAlertCircle />} title="Could not load the pipelines">
          {error.message}
        </Alert>
      )}
      {pipelines?.length === 0 && (
        <Text c="dimmed">
          No pipelines are installed yet. A pipeline comes from a package:{' '}
          <Text component={Link} to="/packages" c="blue" inherit>
            install or upload one
          </Text>{' '}
          and its pipelines appear here, ready to run.
        </Text>
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

      <UploadPackageModal
        opened={uploading}
        onClose={close}
        onUploaded={(packageId) => navigate(`/packages?search=${encodeURIComponent(packageId)}`)}
      />
    </Stack>
  );
}
