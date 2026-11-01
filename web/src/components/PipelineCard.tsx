import { ActionIcon, Badge, Button, Card, Group, Menu, Stack, Text } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { IconDots, IconEdit, IconHistory, IconPackage, IconPlayerPlay, IconTrash } from '@tabler/icons-react';
import { Link } from 'react-router-dom';
import type { Pipeline } from '../api/types';
import { formatRelative } from '../lib/format';
import { pipelineLabel } from '../lib/pipelineLabels';
import { ClearHistoryModal } from './ClearHistoryModal';
import { RenamePipelineModal } from './RenamePipelineModal';
import { StatusBadge } from './StatusBadge';

/** One pipeline: how it is doing, a button to run it, and a menu for everything else you do to a pipeline. */
export function PipelineCard({
  pipeline,
  now,
  onRun,
  running,
}: {
  pipeline: Pipeline;
  now: number;
  onRun: () => void;
  running: boolean;
}) {
  const [renaming, rename] = useDisclosure();
  const [clearing, clear] = useDisclosure();

  return (
    <Card withBorder padding="lg" radius="md">
      <Stack gap="xs" h="100%" justify="space-between">
        <div>
          <Group justify="space-between" wrap="nowrap" align="flex-start">
            <Text fw={600} size="lg" truncate title={pipelineLabel(pipeline)}>
              {pipelineLabel(pipeline)}
            </Text>
            <Group gap={4} wrap="nowrap">
              {pipeline.activeRuns > 0 && (
                <Badge color="blue" variant="filled" component={Link} to={`/runs?pipeline=${pipeline.id}`} style={{ cursor: 'pointer' }}>
                  {pipeline.activeRuns} running
                </Badge>
              )}
              <Menu position="bottom-end" withinPortal>
                <Menu.Target>
                  <ActionIcon variant="subtle" color="gray" aria-label={`Actions for ${pipelineLabel(pipeline)}`}>
                    <IconDots size={18} />
                  </ActionIcon>
                </Menu.Target>
                <Menu.Dropdown>
                  <Menu.Item component={Link} to={`/runs?pipeline=${pipeline.id}`} leftSection={<IconHistory size={16} />}>
                    Run history
                  </Menu.Item>
                  <Menu.Item leftSection={<IconEdit size={16} />} onClick={rename.open}>
                    Rename
                  </Menu.Item>
                  <Menu.Item component={Link} to={`/packages?search=${encodeURIComponent(pipeline.packageId)}`} leftSection={<IconPackage size={16} />}>
                    Go to the package
                  </Menu.Item>
                  <Menu.Divider />
                  <Menu.Item color="red" leftSection={<IconTrash size={16} />} onClick={clear.open}>
                    Clear history...
                  </Menu.Item>
                </Menu.Dropdown>
              </Menu>
            </Group>
          </Group>
          {pipeline.displayName && (
            <Text size="xs" c="dimmed" ff="monospace" truncate title="The pipeline's own name">
              {pipeline.name}
            </Text>
          )}
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
              <Text size="xs" c="blue" component={Link} to={`/runs?pipeline=${pipeline.id}`}>
                history
              </Text>
            </>
          ) : (
            <Text size="xs" c="dimmed">
              Never run
            </Text>
          )}
        </Group>

        <Button leftSection={<IconPlayerPlay size={16} />} onClick={onRun} loading={running}>
          Run now
        </Button>
      </Stack>

      <RenamePipelineModal pipeline={pipeline} opened={renaming} onClose={rename.close} />
      <ClearHistoryModal pipeline={pipeline} opened={clearing} onClose={clear.close} />
    </Card>
  );
}
