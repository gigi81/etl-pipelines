import { Alert, Button, Group, Loader, Stack, Table, Text, TextInput, Title } from '@mantine/core';
import { useDebouncedValue, useDisclosure } from '@mantine/hooks';
import { IconAlertCircle, IconSearch, IconUpload } from '@tabler/icons-react';
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { usePackages } from '../api/hooks';
import { PackageRow } from '../components/PackageRow';
import { UploadPackageModal } from '../components/UploadPackageModal';

/**
 * The packages: what the feed offers and what is installed. A pipeline comes from a package, so this
 * is where pipelines are added - installed from the feed, or uploaded to it first.
 */
export function PackagesPage() {
  // /packages?search=Contoso.Orders opens the page already filtered (an upload from elsewhere lands there).
  const [params] = useSearchParams();
  const [search, setSearch] = useState(params.get('search') ?? '');
  const [debounced] = useDebouncedValue(search.trim(), 300);
  const { data, error, isLoading } = usePackages(debounced);
  const [uploading, { open, close }] = useDisclosure();

  return (
    <Stack>
      <Group justify="space-between">
        <Title order={2}>Packages</Title>
        <Button leftSection={<IconUpload size={16} />} onClick={open}>
          Upload a package
        </Button>
      </Group>

      <Text size="sm" c="dimmed">
        A pipeline comes from a package. Install one here, then{' '}
        <Text component={Link} to="/pipelines" size="sm" c="blue">
          run its pipelines
        </Text>
        . Pipelines that need connection strings or credentials read them from the{' '}
        <Text component={Link} to="/configuration" size="sm" c="blue">
          configuration
        </Text>
        .
      </Text>

      <TextInput
        placeholder="Search packages"
        leftSection={<IconSearch size={14} />}
        value={search}
        onChange={(event) => setSearch(event.currentTarget.value)}
        maw={420}
      />

      {isLoading && <Loader />}
      {error && (
        <Alert color="red" icon={<IconAlertCircle />} title="Could not load the packages">
          {error.message}
        </Alert>
      )}
      {data?.feedError && (
        <Alert color="orange" icon={<IconAlertCircle />} title="The package feed could not be reached">
          {data.feedError} Installed packages are listed, but nothing is known about what the feed offers.
        </Alert>
      )}
      {data?.packages.length === 0 && !data.feedError && (
        <Text c="dimmed">
          {debounced ? `No package matches "${debounced}".` : 'The feed has no packages yet. Upload one to get started.'}
        </Text>
      )}

      {!!data?.packages.length && (
        <Table.ScrollContainer minWidth={760}>
          <Table verticalSpacing="sm">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Package</Table.Th>
                <Table.Th>Installed</Table.Th>
                <Table.Th>Latest on the feed</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {data.packages.map((pkg) => (
                <PackageRow key={pkg.id} pkg={pkg} />
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}

      <UploadPackageModal opened={uploading} onClose={close} onUploaded={setSearch} />
    </Stack>
  );
}
