import { useCallback, useState } from 'react';
import { ActionIcon, Alert, AppShell, Button, CloseButton, Group, Kbd, Loader, ScrollArea, Stack, Tabs, Text, Title, Tooltip } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import type { Api } from './platform/api';
import { ViewRenderer } from './platform/views/ViewRenderer';
import { WorkspaceProvider, useWorkspace } from './platform/workspace/WorkspaceProvider';
import type { Panel } from './platform/workspace/workspaceStore';
import { CommandPalette, openPalette } from './shell/CommandPalette';
import { ContextBar } from './shell/ContextBar';
import { RunDialog } from './shell/RunDialog';
import { AppDialogs, AppPanel } from './apps';
import { RunPanel } from './shell/RunPanel';
import { registerGridFocus } from './shell/focus';
import { useMantineColorScheme } from '@mantine/core';

export function App({ api, initialContext }: { readonly api: Api; readonly initialContext: Readonly<Record<string, unknown>> }) {
  const catalog = useQuery({ queryKey: ['catalog'], queryFn: () => api.catalog(), staleTime: Infinity });
  const [notice, setNotice] = useState<string | undefined>(undefined);
  const notify = useCallback((message: string) => setNotice(message), []);

  if (catalog.isPending) return <Group p="xl"><Loader size="sm" /><Text>Loading the capability catalog…</Text></Group>;
  if (catalog.isError) return <Alert m="xl" color="red" title="The catalog could not be loaded">{catalog.error.message}</Alert>;

  return (
    <WorkspaceProvider api={api} catalog={catalog.data} initialContext={initialContext} notify={notify}>
      <Shell notice={notice} clearNotice={() => setNotice(undefined)} />
    </WorkspaceProvider>
  );
}

function Shell({ notice, clearNotice }: { readonly notice: string | undefined; readonly clearNotice: () => void }) {
  const { colorScheme, toggleColorScheme } = useMantineColorScheme();
  return (
    <AppShell header={{ height: { base: 'auto', sm: 56 } }} padding="sm">
      <AppShell.Header px="sm" py={6}>
        <Group justify="space-between" wrap="wrap" gap="xs" h="100%">
          <Group gap="sm" wrap="nowrap">
            <Title order={1} size="sm" c="signal" style={{ letterSpacing: '0.08em' }}>QUANT WORKBENCH</Title>
            <Button size="compact-xs" variant="default" onClick={openPalette} aria-label="Open command palette"
              rightSection={<Kbd size="xs">Ctrl K</Kbd>}>Commands</Button>
          </Group>
          <Group gap="xs" wrap="wrap">
            <ContextBar />
            <Tooltip label="Toggle theme (Ctrl+Alt+T)">
              <ActionIcon variant="default" size="sm" onClick={toggleColorScheme} aria-label="Toggle theme">
                {colorScheme === 'dark' ? '☀' : '☾'}
              </ActionIcon>
            </Tooltip>
          </Group>
        </Group>
      </AppShell.Header>
      <AppShell.Main>
        {notice !== undefined && (
          <Alert color="yellow" variant="light" mb="sm" role="status" withCloseButton onClose={clearNotice} data-testid="notice">{notice}</Alert>
        )}
        <Panels />
      </AppShell.Main>
      <CommandPalette />
      <RunDialog />
      <AppDialogs />
    </AppShell>
  );
}

function Panels() {
  const panels = useWorkspace((s) => s.panels);
  const activePanelId = useWorkspace((s) => s.activePanelId);
  const activate = useWorkspace((s) => s.activate);
  const closePanel = useWorkspace((s) => s.closePanel);

  if (panels.length === 0)
    return (
      <Stack align="center" mt="xl" gap="xs">
        <Text c="dimmed">Nothing open yet.</Text>
        <Text c="dimmed" size="sm">Press <Kbd>Ctrl</Kbd> + <Kbd>K</Kbd> and run a workflow, start a New XVA workspace or Open Pricing DB.</Text>
      </Stack>
    );

  return (
    <Tabs value={activePanelId ?? null} onChange={(id) => id !== null && activate(id)} keepMounted={false} variant="outline">
      <ScrollArea type="auto" scrollbarSize={6}>
        <Tabs.List style={{ flexWrap: 'nowrap' }}>
          {panels.map((p) => (
            <Tabs.Tab key={p.id} value={p.id} rightSection={
              <CloseButton size="xs" component="span" aria-label={`Close ${p.title}`} onClick={(e) => { e.stopPropagation(); closePanel(p.id); }} />
            }>{p.title}</Tabs.Tab>
          ))}
        </Tabs.List>
      </ScrollArea>
      {panels.map((p) => (
        <Tabs.Panel key={p.id} value={p.id} pt="sm"><PanelContent panel={p} /></Tabs.Panel>
      ))}
    </Tabs>
  );
}

function PanelContent({ panel }: { readonly panel: Panel }) {
  const registerFocus = useCallback((focus: (() => void) | undefined) => registerGridFocus(panel.id, focus), [panel.id]);
  if (panel.kind === 'run') return <RunPanel panel={panel} />;
  if (panel.kind === 'app') return <AppPanel panel={panel} />;
  return <ViewRenderer view={panel.view} outputs={{}} registerFocus={registerFocus} />;
}
