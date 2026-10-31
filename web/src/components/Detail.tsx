import { Group, Text } from '@mantine/core';

/** One "label  value" row of a card. */
export function Detail({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <Group gap="xs" wrap="nowrap" align="flex-start">
      <Text size="sm" c="dimmed" w={88} style={{ flexShrink: 0 }}>
        {label}
      </Text>
      <Text size="sm" component="div" style={{ wordBreak: 'break-word' }}>
        {children}
      </Text>
    </Group>
  );
}
