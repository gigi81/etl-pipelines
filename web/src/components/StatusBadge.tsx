import { Badge, Loader } from '@mantine/core';
import type { RunStatus } from '../api/types';

const colors: Record<RunStatus, string> = {
  Queued: 'gray',
  Dispatched: 'cyan',
  Running: 'blue',
  Succeeded: 'green',
  Failed: 'red',
  AgentLost: 'orange',
};

const labels: Record<RunStatus, string> = {
  Queued: 'Queued',
  Dispatched: 'Dispatched',
  Running: 'Running',
  Succeeded: 'Succeeded',
  Failed: 'Failed',
  AgentLost: 'Agent lost',
};

export function StatusBadge({ status }: { status: RunStatus }) {
  return (
    <Badge
      color={colors[status]}
      variant="light"
      miw="max-content"
      leftSection={status === 'Running' ? <Loader size={10} color={colors[status]} /> : undefined}
    >
      {labels[status]}
    </Badge>
  );
}
