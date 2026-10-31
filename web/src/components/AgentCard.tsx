import { Anchor, Badge, Card, Code, Divider, Group, Stack, Text } from '@mantine/core';
import { IconCpu, IconNetwork, IconServer } from '@tabler/icons-react';
import { Link } from 'react-router-dom';
import { useNow } from '../api/hooks';
import type { Agent } from '../api/types';
import { extraInfo } from '../lib/agentInfo';
import { formatDateTime, formatDuration, formatRelative } from '../lib/format';
import { Detail } from './Detail';
import { RunProgress } from './RunProgress';
import { StatusBadge } from './StatusBadge';

/** One agent: status, host details, and the runs it is working on. */
export function AgentCard({ agent }: { agent: Agent }) {
  const now = useNow(5000);
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
          {agent.tags.length > 0 && (
            <Detail label="Tags">
              <Group gap={4}>
                {agent.tags.map((tag) => (
                  <Badge key={tag} size="sm" variant="outline">
                    {tag}
                  </Badge>
                ))}
              </Group>
            </Detail>
          )}
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
                    {run.pipelineName}
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
      </Stack>
    </Card>
  );
}
