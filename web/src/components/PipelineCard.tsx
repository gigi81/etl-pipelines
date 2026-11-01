import { ActionIcon, Badge, Button, Card, Group, Menu, Stack, Text } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { IconAlertTriangle, IconDots, IconEdit, IconHistory, IconPackage, IconPlayerPlay, IconServer, IconTrash } from '@tabler/icons-react';
import { Link } from 'react-router-dom';
import { useAgents } from '../api/hooks';
import type { Pipeline } from '../api/types';
import { describeAssignment, eligibleAgents, isAnyAgent } from '../lib/assignment';
import { formatRelative } from '../lib/format';
import { pipelineLabel } from '../lib/pipelineLabels';
import { AssignAgentsModal } from './AssignAgentsModal';
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
  const [assigning, assign] = useDisclosure();
  const { data: agents = [] } = useAgents();
  const restricted = !isAnyAgent(pipeline.assignment);
  // Only meaningful once the agents are loaded: until then nothing is known to be missing.
  const nobodyOnline = restricted && agents.length > 0 && !eligibleAgents(pipeline.assignment, agents).some((agent) => agent.status === 'Online');

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
                  <Menu.Item leftSection={<IconServer size={16} />} onClick={assign.open}>
                    Choose agents...
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
          {restricted && (
            <Group gap={4} wrap="nowrap" mt={2}>
              {nobodyOnline ? <IconAlertTriangle size={14} color="var(--mantine-color-orange-6)" /> : <IconServer size={14} />}
              <Text
                size="xs"
                c={nobodyOnline ? 'orange' : 'dimmed'}
                truncate
                title={nobodyOnline ? 'No agent that may run it is online' : 'The agents that may run it'}
              >
                Runs on: {describeAssignment(pipeline.assignment, agents)}
              </Text>
            </Group>
          )}
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
      <AssignAgentsModal pipeline={pipeline} opened={assigning} onClose={assign.close} />
      <ClearHistoryModal pipeline={pipeline} opened={clearing} onClose={clear.close} />
    </Card>
  );
}
