import { useCallback, useState } from 'react';
import { ActionIcon, Alert, AppShell, Button, CloseButton, Drawer, Group, Kbd, Loader, ScrollArea, Stack, Tabs, Text, Title, Tooltip, em } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
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
import { AiProvider, useAi, useAiContext } from './ai/AiProvider';
import { AiPane } from './ai/AiPane';
import type { AgentTransport } from './ai/agui';

export function App({ api, initialContext, agent }: {
  readonly api: Api;
  readonly initialContext: Readonly<Record<string, unknown>>;
  /** The AI pane's transport; the server's AG-UI endpoint unless a test gives another. */
  readonly agent?: AgentTransport;
}) {
  const catalog = useQuery({ queryKey: ['catalog'], queryFn: () => api.catalog(), staleTime: Infinity });
  const [notice, setNotice] = useState<string | undefined>(undefined);
  const notify = useCallback((message: string) => setNotice(message), []);

  if (catalog.isPending) return <Group p="xl"><Loader size="sm" /><Text>Loading the capability catalog…</Text></Group>;
  if (catalog.isError) return <Alert m="xl" color="red" title="The catalog could not be loaded">{catalog.error.message}</Alert>;

  return (
    <WorkspaceProvider api={api} catalog={catalog.data} initialContext={initialContext} notify={notify}>
      <AiProvider {...(agent === undefined ? {} : { transport: agent })}>
        <Shell notice={notice} clearNotice={() => setNotice(undefined)} />
      </AiProvider>
    </WorkspaceProvider>
  );
}

function Shell({ notice, clearNotice }: { readonly notice: string | undefined; readonly clearNotice: () => void }) {
  const { colorScheme, toggleColorScheme } = useMantineColorScheme();
  const aiOpen = useAi((s) => s.open);
  const ai = useAiContext().store;
  // The AI pane is beside the panels (ADR-0007 §7). On a phone, where the header wraps to its own height and there is
  // no room beside, it is a drawer over them that does not take the keyboard when it opens.
  const phone = useMediaQuery(`(max-width: ${em(767.98)})`, false, { getInitialValueInEffect: false });
  return (
    <AppShell header={{ height: { base: 'auto', sm: 56 } }} padding="sm"
      aside={{ width: { sm: 340, lg: 400 }, breakpoint: 'sm', collapsed: { mobile: true, desktop: !aiOpen || phone } }}>
      <AppShell.Header px="sm" py={6}>
        <Group justify="space-between" wrap="wrap" gap="xs" h="100%">
          <Group gap="sm" wrap="nowrap">
            <Title order={1} size="sm" c="signal" style={{ letterSpacing: '0.08em' }}>QUANT WORKBENCH</Title>
            <Button size="compact-xs" variant="default" onClick={openPalette} aria-label="Open command line"
              rightSection={<Kbd size="xs">Ctrl K</Kbd>}>Commands</Button>
          </Group>
          <Group gap="xs" wrap="wrap">
            <ContextBar />
            <Button size="compact-xs" variant={aiOpen ? 'filled' : 'default'} onClick={() => ai.getState().setOpen(!aiOpen)}
              aria-pressed={aiOpen} aria-label={aiOpen ? 'Hide the AI pane' : 'Show the AI pane'}>AI</Button>
            <Tooltip label="Toggle theme (toggle theme)">
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
      <AppShell.Aside>{aiOpen && !phone && <AiPane />}</AppShell.Aside>
      <Drawer opened={aiOpen && phone} onClose={() => ai.getState().setOpen(false)} position="right" size="100%" withCloseButton={false}
        trapFocus={false} returnFocus={false} padding={0} transitionProps={{ duration: 0 }} aria-label="AI"
        styles={{ content: { display: 'flex', flexDirection: 'column' }, body: { flex: 1, minHeight: 0 } }}>
        {aiOpen && phone && <AiPane />}
      </Drawer>
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
        <Text c="dimmed" size="sm">Press <Kbd>Ctrl</Kbd> + <Kbd>K</Kbd> and type what you want to do: <Kbd>new rates</Kbd>, <Kbd>new xva</Kbd>, <Kbd>open pricing db</Kbd>, or <Kbd>?</Kbd> for every command.</Text>
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
