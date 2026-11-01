import { Button, Group, PasswordInput, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconDeviceFloppy } from '@tabler/icons-react';
import { useSetConfiguration } from '../api/hooks';

/** Sets one configuration entry. Setting a key that exists replaces its value, which is never shown. */
export function ConfigurationEntryForm({
  entryKey,
  onKeyChange,
  value,
  onValueChange,
}: {
  entryKey: string;
  onKeyChange: (key: string) => void;
  value: string;
  onValueChange: (value: string) => void;
}) {
  const save = useSetConfiguration();

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    save.mutate(
      { key: entryKey.trim(), value },
      {
        onSuccess: () => {
          notifications.show({ color: 'green', title: 'Saved', message: `${entryKey.trim()} is set.` });
          onKeyChange('');
          onValueChange('');
        },
        onError: (error) => notifications.show({ color: 'red', title: 'Could not save', message: error.message }),
      },
    );
  };

  return (
    <form onSubmit={submit}>
      <Group align="flex-end" wrap="wrap">
        <TextInput
          label="Key"
          description="e.g. ConnectionStrings:sales"
          placeholder="ConnectionStrings:sales"
          value={entryKey}
          onChange={(event) => onKeyChange(event.currentTarget.value)}
          w={320}
          required
        />
        <PasswordInput
          label="Value"
          description="Stored encrypted; never shown again"
          value={value}
          onChange={(event) => onValueChange(event.currentTarget.value)}
          w={360}
          autoComplete="off"
          required
        />
        <Button type="submit" leftSection={<IconDeviceFloppy size={16} />} loading={save.isPending} disabled={!entryKey.trim() || !value}>
          Save
        </Button>
      </Group>
    </form>
  );
}
