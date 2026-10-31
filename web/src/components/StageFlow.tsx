import { Box, Group, Loader, Paper, Stack, Text, ThemeIcon, Tooltip } from '@mantine/core';
import { IconAlertTriangle, IconArrowRight, IconCheck, IconCircleDashed, IconPlayerSkipForward } from '@tabler/icons-react';
import type { RunDetail } from '../api/types';
import { buildFlow, type NodeState } from '../lib/buildFlow';
import { formatDuration, formatNumber, isActive } from '../lib/format';

const stateStyle: Record<NodeState, { color: string; icon: React.ReactNode; label: string }> = {
  done: { color: 'green', icon: <IconCheck size={16} />, label: 'Completed' },
  failed: { color: 'red', icon: <IconAlertTriangle size={16} />, label: 'Failed' },
  running: { color: 'blue', icon: <Loader size={14} color="white" />, label: 'Running' },
  pending: { color: 'gray', icon: <IconCircleDashed size={16} />, label: 'Pending' },
  skipped: { color: 'gray', icon: <IconPlayerSkipForward size={16} />, label: 'Not reached' },
};

/** The run's stages as a left-to-right flow, each node showing the rows it took in and passed on. */
export function StageFlow({ detail }: { detail: RunDetail }) {
  const nodes = buildFlow(detail);

  if (nodes.length === 0) {
    return (
      <Text c="dimmed" size="sm">
        {isActive(detail.run.status)
          ? 'Waiting for the pipeline to report its first stage...'
          : 'This run did not report any stages.'}
      </Text>
    );
  }

  return (
    <Box style={{ overflowX: 'auto' }} pb="xs">
      <Group wrap="nowrap" gap="xs" align="stretch">
        {nodes.map((node, index) => {
          const style = stateStyle[node.state];
          const muted = node.state === 'pending' || node.state === 'skipped';
          return (
            <Group key={node.key} wrap="nowrap" gap="xs" align="center">
              {index > 0 && <IconArrowRight size={18} opacity={0.5} />}
              <Paper
                withBorder
                p="sm"
                radius="md"
                w={210}
                h="100%"
                style={{
                  borderColor: `var(--mantine-color-${style.color}-${muted ? 4 : 6})`,
                  borderWidth: node.state === 'running' ? 2 : 1,
                  opacity: muted ? 0.65 : 1,
                }}
                data-testid={`stage-${node.state}`}
              >
                <Stack gap={6}>
                  <Group gap="xs" wrap="nowrap">
                    <Tooltip label={style.label}>
                      <ThemeIcon color={style.color} variant={muted ? 'light' : 'filled'} size="sm" radius="xl">
                        {style.icon}
                      </ThemeIcon>
                    </Tooltip>
                    <Text fw={600} size="sm" truncate title={node.name}>
                      {node.name}
                    </Text>
                  </Group>
                  {node.rowsIn !== undefined ? (
                    <>
                      <Text size="xs" c="dimmed">
                        {formatNumber(node.rowsIn)} in &rarr; {formatNumber(node.rowsOut)} out
                      </Text>
                      <Group gap="xs">
                        {!!node.rowsFailed && (
                          <Text size="xs" c="red">
                            {formatNumber(node.rowsFailed)} failed
                          </Text>
                        )}
                        <Text size="xs" c="dimmed">
                          {formatDuration(node.elapsedMs ?? 0)}
                        </Text>
                      </Group>
                      {node.error && (
                        <Text size="xs" c="red" lineClamp={3} title={node.error}>
                          {node.error}
                        </Text>
                      )}
                    </>
                  ) : (
                    <Text size="xs" c="dimmed">
                      {style.label}
                    </Text>
                  )}
                </Stack>
              </Paper>
            </Group>
          );
        })}
      </Group>
    </Box>
  );
}
