import { Alert, Button, Group, Loader, SimpleGrid, Stack, Text, Title } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { IconAlertCircle, IconPackage, IconUpload } from '@tabler/icons-react';
import { Link, useNavigate } from 'react-router-dom';
import { useNow, usePipelines, useTriggerRun } from '../api/hooks';
import { PipelineCard } from '../components/PipelineCard';
import { UploadPackageModal } from '../components/UploadPackageModal';
import { pipelineLabel } from '../lib/pipelineLabels';

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
          <PipelineCard
            key={pipeline.id}
            pipeline={pipeline}
            now={now}
            onRun={() => run(pipeline.id, pipelineLabel(pipeline))}
            running={trigger.isPending && trigger.variables === pipeline.id}
          />
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
