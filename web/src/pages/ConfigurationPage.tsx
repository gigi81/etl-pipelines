import { ActionIcon, Alert, Code, Loader, Stack, Table, Text, Title, Tooltip } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconAlertCircle, IconEdit, IconInfoCircle, IconTrash } from '@tabler/icons-react';
import { useState } from 'react';
import { useConfigurationKeys, useDeleteConfiguration, useNow } from '../api/hooks';
import { ConfigurationEntryForm } from '../components/ConfigurationEntryForm';
import { formatDateTime, formatRelative } from '../lib/format';

/**
 * The configuration pipelines read as `IConfiguration`: connection strings, SFTP credentials, any
 * setting. Values are secrets - encrypted at rest and only ever handed to a running pipeline - so
 * this lists keys and lets a value be set or replaced, never read.
 */
export function ConfigurationPage() {
  const { data: keys, error, isLoading } = useConfigurationKeys();
  const remove = useDeleteConfiguration();
  const now = useNow(5000);
  const [entryKey, setEntryKey] = useState('');
  const [value, setValue] = useState('');

  return (
    <Stack>
      <Title order={2}>Configuration</Title>

      <Alert color="blue" icon={<IconInfoCircle />} title="What goes here">
        Pipelines name their connections rather than hard-coding them. A pipeline that opens{' '}
        <Code>GetConnectionString("sales")</Code> reads <Code>ConnectionStrings:sales</Code>; an SFTP connection named{' '}
        <Code>vendor</Code> reads <Code>Sftp:vendor:Host</Code>, <Code>Sftp:vendor:UserName</Code>,{' '}
        <Code>Sftp:vendor:Password</Code>. Every entry is given to every run.
      </Alert>

      <ConfigurationEntryForm entryKey={entryKey} onKeyChange={setEntryKey} value={value} onValueChange={setValue} />

      {isLoading && <Loader />}
      {error && (
        <Alert color="red" icon={<IconAlertCircle />} title="Could not load the configuration">
          {error.message}
        </Alert>
      )}
      {keys?.length === 0 && <Text c="dimmed">No entries yet.</Text>}

      {!!keys?.length && (
        <Table verticalSpacing="xs" maw={760}>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Key</Table.Th>
              <Table.Th>Last set</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {keys.map((entry) => (
              <Table.Tr key={entry.key}>
                <Table.Td>
                  <Code>{entry.key}</Code>
                </Table.Td>
                <Table.Td title={formatDateTime(entry.updatedAtUtc)}>{formatRelative(entry.updatedAtUtc, now)}</Table.Td>
                <Table.Td ta="right">
                  <Tooltip label="Replace the value">
                    <ActionIcon variant="subtle" aria-label={`Replace ${entry.key}`} onClick={() => setEntryKey(entry.key)}>
                      <IconEdit size={16} />
                    </ActionIcon>
                  </Tooltip>
                  <Tooltip label="Delete">
                    <ActionIcon
                      variant="subtle"
                      color="red"
                      aria-label={`Delete ${entry.key}`}
                      loading={remove.isPending && remove.variables === entry.key}
                      onClick={() =>
                        remove.mutate(entry.key, {
                          onSuccess: () => notifications.show({ color: 'green', title: 'Deleted', message: `${entry.key} was removed.` }),
                          onError: (failure) => notifications.show({ color: 'red', title: 'Could not delete', message: failure.message }),
                        })
                      }
                    >
                      <IconTrash size={16} />
                    </ActionIcon>
                  </Tooltip>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
    </Stack>
  );
}
