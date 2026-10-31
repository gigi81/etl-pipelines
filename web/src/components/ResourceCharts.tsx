import { AreaChart } from '@mantine/charts';
import { Group, Paper, Text } from '@mantine/core';
import type { ResourceSample } from '../api/types';
import { formatBytes } from '../lib/format';

/** CPU and memory of the run's pipeline process, as sampled by the agent running it. */
export function ResourceCharts({ samples }: { samples: ResourceSample[] }) {
  if (samples.length === 0) {
    return (
      <Text c="dimmed" size="sm">
        No resource samples yet - the agent reports one every few seconds while the process runs.
      </Text>
    );
  }

  const data = samples.map((sample) => ({
    time: new Date(sample.sampledAtUtc).toLocaleTimeString(),
    cpu: Math.round(sample.cpuPercent * 10) / 10,
    memory: Math.round((sample.workingSetBytes / (1024 * 1024)) * 10) / 10,
  }));
  const latest = samples[samples.length - 1];

  return (
    <Group grow align="stretch" gap="md">
      <Paper withBorder p="md" radius="md">
        <Group justify="space-between" mb="xs">
          <Text fw={600} size="sm">CPU</Text>
          <Text size="sm" c="dimmed">{latest.cpuPercent.toFixed(1)}%</Text>
        </Group>
        <AreaChart
          h={140}
          data={data}
          dataKey="time"
          series={[{ name: 'cpu', label: 'CPU %', color: 'blue.6' }]}
          withDots={false}
          withXAxis={false}
          curveType="monotone"
          yAxisProps={{ domain: [0, 100] }}
        />
      </Paper>
      <Paper withBorder p="md" radius="md">
        <Group justify="space-between" mb="xs">
          <Text fw={600} size="sm">Memory</Text>
          <Text size="sm" c="dimmed">{formatBytes(latest.workingSetBytes)}</Text>
        </Group>
        <AreaChart
          h={140}
          data={data}
          dataKey="time"
          series={[{ name: 'memory', label: 'Working set (MiB)', color: 'teal.6' }]}
          withDots={false}
          withXAxis={false}
          curveType="monotone"
        />
      </Paper>
    </Group>
  );
}
