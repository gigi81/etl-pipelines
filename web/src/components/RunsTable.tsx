import { Anchor, Table, Text } from '@mantine/core';
import { Link } from 'react-router-dom';
import type { RunSummary } from '../api/types';
import { formatDateTime, formatDuration, formatNumber, formatRelative, isActive, runDurationMs } from '../lib/format';
import { useNow } from '../api/hooks';
import { RunProgress } from './RunProgress';
import { StatusBadge } from './StatusBadge';

export function RunsTable({ runs, showProgress }: { runs: RunSummary[]; showProgress?: boolean }) {
  const now = useNow();

  return (
    <Table.ScrollContainer minWidth={760}>
      <Table highlightOnHover verticalSpacing="sm">
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Pipeline</Table.Th>
            <Table.Th>Status</Table.Th>
            {showProgress && <Table.Th>Progress</Table.Th>}
            <Table.Th>Agent</Table.Th>
            <Table.Th>Started</Table.Th>
            <Table.Th>Duration</Table.Th>
            <Table.Th ta="right">Rows read</Table.Th>
            <Table.Th ta="right">Rows written</Table.Th>
            <Table.Th ta="right">Rows failed</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {runs.map((run) => (
            <Table.Tr key={run.id}>
              <Table.Td>
                <Anchor component={Link} to={`/runs/${run.id}`} fw={500}>
                  {run.pipelineName}
                </Anchor>
                <Text size="xs" c="dimmed">
                  {run.packageId} {run.packageVersion}
                </Text>
              </Table.Td>
              <Table.Td>
                <StatusBadge status={run.status} />
              </Table.Td>
              {showProgress && (
                <Table.Td>
                  <RunProgress run={run} />
                </Table.Td>
              )}
              <Table.Td>{run.agentName ?? <Text c="dimmed">-</Text>}</Table.Td>
              <Table.Td title={formatDateTime(run.startedAtUtc ?? run.requestedAtUtc)}>
                {formatRelative(run.startedAtUtc ?? run.requestedAtUtc, now)}
              </Table.Td>
              <Table.Td>{formatDuration(runDurationMs(run, isActive(run.status) ? now : undefined))}</Table.Td>
              <Table.Td ta="right">{formatNumber(run.rowsRead)}</Table.Td>
              <Table.Td ta="right">{formatNumber(run.rowsWritten)}</Table.Td>
              <Table.Td ta="right" c={run.rowsFailed ? 'red' : undefined}>
                {formatNumber(run.rowsFailed)}
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}
