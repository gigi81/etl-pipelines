import { ActionIcon, AppShell, Badge, Burger, Group, Loader, NavLink, Title, useComputedColorScheme, useMantineColorScheme } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { IconMoon, IconPackage, IconPlayerPlay, IconServer, IconSettings, IconSun, IconTopologyStar3 } from '@tabler/icons-react';
import { Suspense, lazy } from 'react';
import { Link, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { useRuns } from './api/hooks';
import { useLiveUpdates } from './api/useLiveUpdates';
import { AgentsPage } from './pages/AgentsPage';
import { ConfigurationPage } from './pages/ConfigurationPage';
import { PackagesPage } from './pages/PackagesPage';
import { PipelinesPage } from './pages/PipelinesPage';
import { RunsPage } from './pages/RunsPage';

// Pulls in the charting library, which most visits never need.
const RunDetailPage = lazy(() => import('./pages/RunDetailPage').then((m) => ({ default: m.RunDetailPage })));

export function App() {
  const [opened, { toggle, close }] = useDisclosure();
  const { pathname } = useLocation();
  const { setColorScheme } = useMantineColorScheme();
  const scheme = useComputedColorScheme('light');
  const running = useRuns({ active: true });
  const live = useLiveUpdates();

  const links = [
    { to: '/pipelines', label: 'Pipelines', icon: <IconTopologyStar3 size={18} />, match: '/pipelines' },
    {
      to: '/runs',
      label: 'Runs',
      icon: <IconPlayerPlay size={18} />,
      match: '/runs',
      badge: running.data?.length || undefined,
    },
    { to: '/agents', label: 'Agents', icon: <IconServer size={18} />, match: '/agents' },
    { to: '/packages', label: 'Packages', icon: <IconPackage size={18} />, match: '/packages' },
    { to: '/configuration', label: 'Configuration', icon: <IconSettings size={18} />, match: '/configuration' },
  ];

  return (
    <AppShell header={{ height: 56 }} navbar={{ width: 220, breakpoint: 'sm', collapsed: { mobile: !opened } }} padding="md">
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between">
          <Group>
            <Burger opened={opened} onClick={toggle} hiddenFrom="sm" size="sm" />
            <Title order={3}>ETL Pipelines</Title>
          </Group>
          <Group gap="sm">
            <Badge
              variant="dot"
              color={live ? 'green' : 'yellow'}
              title={live ? 'Changes are pushed from the server as they happen' : 'Reconnecting - what you see may be out of date'}
            >
              {live ? 'Live' : 'Reconnecting'}
            </Badge>
            <ActionIcon
              variant="default"
              aria-label="Toggle color scheme"
              onClick={() => setColorScheme(scheme === 'dark' ? 'light' : 'dark')}
            >
              {scheme === 'dark' ? <IconSun size={18} /> : <IconMoon size={18} />}
            </ActionIcon>
          </Group>
        </Group>
      </AppShell.Header>

      <AppShell.Navbar p="xs">
        {links.map((link) => (
          <NavLink
            key={link.to}
            component={Link}
            to={link.to}
            label={link.label}
            leftSection={link.icon}
            rightSection={link.badge ? <Badge size="sm" circle>{link.badge}</Badge> : undefined}
            active={pathname.startsWith(link.match)}
            onClick={close}
          />
        ))}
      </AppShell.Navbar>

      <AppShell.Main>
        <Suspense fallback={<Loader />}>
        <Routes>
          <Route path="/" element={<Navigate to="/pipelines" replace />} />
          <Route path="/pipelines" element={<PipelinesPage />} />
          <Route path="/runs" element={<RunsPage />} />
          <Route path="/runs/:runId" element={<RunDetailPage />} />
          <Route path="/agents" element={<AgentsPage />} />
          <Route path="/packages" element={<PackagesPage />} />
          <Route path="/configuration" element={<ConfigurationPage />} />
          <Route path="*" element={<Navigate to="/pipelines" replace />} />
        </Routes>
        </Suspense>
      </AppShell.Main>
    </AppShell>
  );
}
