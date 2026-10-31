import { Paper, Text } from '@mantine/core';

/** A labelled figure in a bordered tile. */
export function Stat({ label, value, color }: { label: string; value: React.ReactNode; color?: string }) {
  return (
    <Paper withBorder p="md" radius="md">
      <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
        {label}
      </Text>
      <Text size="xl" fw={600} c={color}>
        {value}
      </Text>
    </Paper>
  );
}
