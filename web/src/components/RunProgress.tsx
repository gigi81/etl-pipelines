import { Progress, Text } from '@mantine/core';
import type { RunSummary } from '../api/types';
import { isActive, runProgress } from '../lib/format';

/** A progress bar from the stages completed so far, indeterminate when the run has no history to compare to. */
export function RunProgress({ run }: { run: RunSummary }) {
  const percent = runProgress(run);
  const active = isActive(run.status);

  return (
    <div style={{ minWidth: 140 }}>
      <Progress
        value={percent ?? 100}
        animated={active}
        striped={active && percent === null}
        color={run.status === 'Failed' ? 'red' : run.status === 'AgentLost' ? 'orange' : undefined}
        size="sm"
      />
      <Text size="xs" c="dimmed" mt={2}>
        {run.stagesCompleted}
        {run.stagesExpected ? ` / ${run.stagesExpected}` : ''} stage{run.stagesCompleted === 1 && !run.stagesExpected ? '' : 's'}
      </Text>
    </div>
  );
}
