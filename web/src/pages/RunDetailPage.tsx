import { Alert, Anchor, Badge, Group, Loader, SimpleGrid, Stack, Text, Title } from '@mantine/core';
import { IconAlertCircle, IconPlugConnectedX } from '@tabler/icons-react';
import { Link, useParams } from 'react-router-dom';
import { useNow, useRunStream } from '../api/hooks';
import { Stat } from '../components/Stat';
import { ResourceCharts } from '../components/ResourceCharts';
import { RunProgress } from '../components/RunProgress';
import { StageFlow } from '../components/StageFlow';
import { StatusBadge } from '../components/StatusBadge';
import { formatDateTime, formatDuration, formatNumber, isActive, runDurationMs } from '../lib/format';

export function RunDetailPage() {
  const { runId = '' } = useParams();
  const { detail, notFound, reconnecting } = useRunStream(runId);
  const now = useNow();

  if (notFound) {
    return (
      <Alert color="red" icon={<IconAlertCircle />} title="Run not found">
        No run with id {runId} exists. <Anchor component={Link} to="/runs">Back to the runs</Anchor>
      </Alert>
    );
  }

  if (!detail) return <Loader />;

  const { run } = detail;
  const active = isActive(run.status);
  const failedStage = detail.stages.find((stage) => stage.errorCode);

  return (
    <Stack>
      <Group justify="space-between" align="flex-start">
        <div>
          <Group gap="sm">
            <Title order={2}>{run.pipelineName}</Title>
            <StatusBadge status={run.status} />
            {reconnecting && (
              <Badge color="yellow" variant="light" leftSection={<IconPlugConnectedX size={12} />}>
                Reconnecting...
              </Badge>
            )}
          </Group>
          <Text c="dimmed" size="sm">
            {run.packageId} {run.packageVersion} &middot; run {run.id}
          </Text>
        </div>
        {active && (
          <div style={{ minWidth: 220 }}>
            <RunProgress run={run} />
          </div>
        )}
      </Group>

      {run.status === 'AgentLost' && (
        <Alert color="orange" icon={<IconAlertCircle />} title="The agent stopped responding">
          The agent running this pipeline went silent, so the run was given up on. It is not retried automatically,
          as a partial load may not be safe to repeat.
        </Alert>
      )}
      {run.status === 'Failed' && (
        <Alert color="red" icon={<IconAlertCircle />} title="The run failed">
          {failedStage
            ? `Stage "${failedStage.name}": ${[failedStage.errorCode, failedStage.errorDescription].filter(Boolean).join(': ')}`
            : `The pipeline process exited with code ${run.exitCode ?? 'unknown'}.`}
        </Alert>
      )}

      <SimpleGrid cols={{ base: 2, sm: 3, lg: 6 }}>
        <Stat label="Duration" value={formatDuration(runDurationMs(run, active ? now : undefined))} />
        <Stat
          label="Agent"
          value={
            run.agentId ? (
              <Anchor component={Link} to="/agents" inherit>
                {run.agentName}
              </Anchor>
            ) : (
              '-'
            )
          }
        />
        <Stat label="Started" value={<Text span inherit title={formatDateTime(run.startedAtUtc)}>{run.startedAtUtc ? new Date(run.startedAtUtc).toLocaleTimeString() : '-'}</Text>} />
        <Stat label="Rows read" value={formatNumber(run.rowsRead)} />
        <Stat label="Rows written" value={formatNumber(run.rowsWritten)} />
        <Stat label="Rows failed" value={formatNumber(run.rowsFailed)} color={run.rowsFailed ? 'red' : undefined} />
      </SimpleGrid>

      <Title order={4}>Steps</Title>
      <StageFlow detail={detail} />

      <Title order={4}>Resources</Title>
      <ResourceCharts samples={detail.resourceSamples} />
    </Stack>
  );
}
