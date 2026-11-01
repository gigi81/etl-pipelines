import { Alert, Button, FileInput, Group, Modal, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconAlertCircle, IconUpload } from '@tabler/icons-react';
import { useState } from 'react';
import { useUploadPackage } from '../api/hooks';

/**
 * Uploads a built pipeline package (a .NET tool, `.nupkg`) to the package feed. It is not installed
 * by uploading it: it then shows up in the list, ready to be installed like any other.
 */
export function UploadPackageModal({
  opened,
  onClose,
  onUploaded,
}: {
  opened: boolean;
  onClose: () => void;
  /** Called with the id of the package that was uploaded. */
  onUploaded: (packageId: string) => void;
}) {
  const upload = useUploadPackage();
  const [file, setFile] = useState<File | null>(null);

  const close = () => {
    setFile(null);
    upload.reset();
    onClose();
  };

  const submit = () => {
    if (!file) return;
    upload.mutate(file, {
      onSuccess: (uploaded) => {
        notifications.show({
          color: 'green',
          title: `Uploaded ${uploaded.id} ${uploaded.version}`,
          message: 'It is on the feed now. Install it from the list.',
        });
        onUploaded(uploaded.id);
        close();
      },
    });
  };

  return (
    <Modal opened={opened} onClose={close} title="Upload a package" centered>
      <Stack>
        <Text size="sm" c="dimmed">
          A pipeline package is a console application built on EtlPipelines.Hosting and packed as a .NET tool
          (<code>&lt;PackAsTool&gt;true&lt;/PackAsTool&gt;</code>). It is pushed to the package feed; nothing is installed or run yet.
        </Text>

        <FileInput
          label="Package"
          placeholder="Choose a .nupkg file"
          accept=".nupkg"
          leftSection={<IconUpload size={14} />}
          value={file}
          onChange={setFile}
          clearable
          disabled={upload.isPending}
        />

        {upload.error && (
          <Alert color="red" icon={<IconAlertCircle />} title="The package was not uploaded">
            {upload.error.message}
          </Alert>
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={close} disabled={upload.isPending}>
            Cancel
          </Button>
          <Button onClick={submit} disabled={!file} loading={upload.isPending} leftSection={<IconUpload size={14} />}>
            Upload
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
