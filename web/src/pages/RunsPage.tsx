import { Alert, Badge, Loader, Stack, Tabs, Text, Title } from '@mantine/core';
import { IconAlertCircle } from '@tabler/icons-react';
import { useState } from 'react';
import { useRuns } from '../api/hooks';
import { RunsTable } from '../components/RunsTable';

export function RunsPage() {
  const [tab, setTab] = useState<string | null>('running');
  const running = useRuns({ active: true });
  const history = useRuns({ active: false, limit: 100 });

  const current = tab === 'history' ? history : running;

  return (
    <Stack>
      <Title order={2}>Runs</Title>
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
