import { Alert, Anchor, Badge, Group, Loader, Stack, Tabs, Text, Title } from '@mantine/core';
import { IconAlertCircle } from '@tabler/icons-react';
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { usePipelineLabeller, usePipelines, useRuns } from '../api/hooks';
import { RunsTable } from '../components/RunsTable';

/** The runs: those in flight, and the history - of everything, or (`/runs?pipeline=<id>`) of one pipeline. */
export function RunsPage() {
  const [tab, setTab] = useState<string | null>('running');
  const [params] = useSearchParams();
  const pipelineId = params.get('pipeline') ?? undefined;
  const { data: pipelines } = usePipelines();
  const label = usePipelineLabeller();
  const pipeline = pipelines?.find((candidate) => candidate.id === pipelineId);

  const running = useRuns({ active: true, pipelineId });
  const history = useRuns({ active: false, limit: 100, pipelineId });

  const current = tab === 'history' ? history : running;

  return (
    <Stack>
      <Title order={2}>Runs</Title>
      {pipelineId && (
        <Group gap="xs">
          <Text size="sm">
            Runs of <b>{pipeline ? label(pipeline.packageId, pipeline.name) : 'one pipeline'}</b>
          </Text>
          <Anchor component={Link} to="/runs" size="sm">
            Show all runs
          </Anchor>
        </Group>
      )}
      <Tabs value={tab} onChange={setTab}>
        <Tabs.List>
          <Tabs.Tab
            value="running"
            rightSection={
              running.data?.length ? (
                <Badge size="sm" circle>
                  {running.data.length}
                </Badge>
              ) : undefined
            }
          >
            Running
          </Tabs.Tab>
          <Tabs.Tab value="history">History</Tabs.Tab>
        </Tabs.List>
      </Tabs>

      {current.isLoading && <Loader />}
      {current.error && (
        <Alert color="red" icon={<IconAlertCircle />} title="Could not load the runs">
          {current.error.message}
        </Alert>
      )}
      {current.data?.length === 0 && (
        <Text c="dimmed">{tab === 'history' ? 'No run has finished yet.' : 'Nothing is running right now.'}</Text>
      )}
      {!!current.data?.length && <RunsTable runs={current.data} showProgress={tab !== 'history'} />}
    </Stack>
  );
}
