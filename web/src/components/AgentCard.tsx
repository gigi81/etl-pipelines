import { Anchor, Badge, Button, Card, Code, Divider, Group, Stack, Text } from '@mantine/core';
import { IconCpu, IconNetwork, IconServer, IconTag, IconTrash } from '@tabler/icons-react';
import { Link } from 'react-router-dom';
import { useDisclosure } from '@mantine/hooks';
import { useNow, usePipelineLabeller } from '../api/hooks';
import type { Agent } from '../api/types';
import { extraInfo } from '../lib/agentInfo';
import { formatDateTime, formatDuration, formatRelative } from '../lib/format';
import { AgentTagsModal } from './AgentTagsModal';
import { Detail } from './Detail';
import { RemoveAgentsModal } from './RemoveAgentsModal';
import { RunProgress } from './RunProgress';
import { StatusBadge } from './StatusBadge';

/** One agent: status, host details, and the runs it is working on. */
export function AgentCard({ agent }: { agent: Agent }) {
  const now = useNow(5000);
  const label = usePipelineLabeller();
  const [removing, { open, close }] = useDisclosure();
  const [tagging, tagEditor] = useDisclosure();
  const online = agent.status === 'Online';
  const info = agent.info;
  const ipAddresses = info?.ipAddresses ?? [];

  return (
    <Card withBorder padding="lg" radius="md">
      <Stack gap="sm">
        <Group justify="space-between" wrap="nowrap">
          <Group gap="xs" wrap="nowrap">
            <IconServer size={20} />
            <Text fw={600} size="lg" truncate>
              {agent.machineName}
            </Text>
          </Group>
          <Badge color={online ? 'green' : 'gray'} variant={online ? 'filled' : 'light'}>
            {agent.status}
          </Badge>
        </Group>

        <Stack gap={4}>
          <Detail label="OS">
            {info?.osDescription ?? '-'}
            {info?.osArchitecture ? ` (${info.osArchitecture})` : ''}
          </Detail>
          <Detail label="IP">
            <Group gap={6}>
              <IconNetwork size={14} />
              {ipAddresses.length ? ipAddresses.map((ip) => <Code key={ip}>{ip}</Code>) : '-'}
            </Group>
          </Detail>
          <Detail label="CPUs">
            <Group gap={6}>
              <IconCpu size={14} />
              {info?.processorCount ?? '-'}
            </Group>
          </Detail>
          <Detail label="Uptime">
            {agent.uptimeSeconds !== null ? formatDuration(agent.uptimeSeconds * 1000) : '-'}
          </Detail>
          <Detail label="Heartbeat">
            <span title={formatDateTime(agent.lastHeartbeatAtUtc)}>{formatRelative(agent.lastHeartbeatAtUtc, now)}</span>
          </Detail>
          <Detail label="Version">{agent.version}</Detail>
          {extraInfo(info).map(([label, value]) => (
            <Detail key={label} label={label}>
              {value}
            </Detail>
          ))}
          <Detail label="Tags">
            <Group gap={4}>
              {agent.tags.map((tag) => {
                // Given in the UI (filled) or reported by the agent's own configuration (outlined).
                const assigned = agent.assignedTags.some((given) => given.toLowerCase() === tag.toLowerCase());
                return (
                  <Badge
                    key={tag}
                    size="sm"
                    tt="none"
                    variant={assigned ? 'light' : 'outline'}
                    title={assigned ? 'Given here' : "From the agent's own configuration"}
                  >
                    {tag}
                  </Badge>
                );
              })}
              <Button size="compact-xs" variant="subtle" leftSection={<IconTag size={12} />} onClick={tagEditor.open}>
                {agent.tags.length === 0 ? 'Add tags' : 'Edit'}
              </Button>
            </Group>
          </Detail>
        </Stack>

        <Divider label={`Running jobs (${agent.activeRuns.length})`} labelPosition="left" />
        {agent.activeRuns.length === 0 ? (
          <Text size="sm" c="dimmed">
            Idle.
          </Text>
        ) : (
          <Stack gap="xs">
            {agent.activeRuns.map((run) => (
              <Group key={run.id} justify="space-between" wrap="nowrap">
                <div style={{ minWidth: 0 }}>
                  <Anchor component={Link} to={`/runs/${run.id}`} size="sm" fw={500}>
                    {label(run.packageId, run.pipelineName)}
                  </Anchor>
                  <div>
                    <StatusBadge status={run.status} />
                  </div>
                </div>
                <RunProgress run={run} />
              </Group>
            ))}
          </Stack>
        )}

        <AgentTagsModal agent={agent} opened={tagging} onClose={tagEditor.close} />

        {!online && (
          <>
            <Divider />
            <Group justify="space-between" wrap="nowrap">
              <Text size="xs" c="dimmed">
                Not responding.
              </Text>
              <Button size="xs" color="red" variant="subtle" leftSection={<IconTrash size={14} />} onClick={open}>
                Remove
              </Button>
            </Group>
            <RemoveAgentsModal agents={[agent]} opened={removing} onClose={close} />
          </>
        )}
      </Stack>
    </Card>
  );
}
