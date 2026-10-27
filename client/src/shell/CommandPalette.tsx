import { useMemo, useState } from 'react';
import { Kbd, useMantineColorScheme } from '@mantine/core';
import { useHotkeys } from '@mantine/hooks';
import { Spotlight, spotlight, type SpotlightActionData, type SpotlightActionGroupData, type SpotlightFilterFunction } from '@mantine/spotlight';
import { assertDistinct, execute, rankCommands, type Command } from '../platform/commands/commands';
import { useQuery } from '@tanstack/react-query';
import type { Catalog, RunRecord } from '../platform/contracts';
import { viewsFor } from '../platform/views/binding';
import { CONTEXT_CONTRACT, useWorkspace, useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import { keyOf, type Panel, type WorkspaceState } from '../platform/workspace/workspaceStore';
import { focusGrid } from './focus';

/**
 * Builds every command the workbench offers right now. Workflows, views and context values come from
 * the catalog, so a module that registers a workflow gets its command without a client change.
 */
export function buildCommands(args: {
  readonly catalog: Catalog;
  readonly state: Pick<WorkspaceState, 'panels' | 'activePanelId' | 'context' | 'openRunDialog' | 'setPanelView' | 'closePanel' | 'cycle' | 'requestContext'>;
  readonly activeRun: RunRecord | undefined;
  readonly toggleTheme: () => void;
  readonly notify: (message: string) => void;
}): Command[] {
  const { catalog, state, activeRun } = args;
  const active: Panel | undefined = state.panels.find((p) => p.id === state.activePanelId);
  const commands: Command[] = [];

  for (const workflow of catalog.workflows)
    commands.push({
      id: `workflow.run:${keyOf(workflow)}`,
      title: `Run ${workflow.title}`,
      group: 'Workflows',
      keywords: [workflow.definitionId],
      ...(state.context.prepared === undefined ? { unavailable: 'The context is not valid.' } : {}),
      run: () => state.openRunDialog(keyOf(workflow)),
    });

  if (active?.kind === 'run' && activeRun !== undefined && activeRun.status !== 'running')
    for (const view of viewsFor(activeRun.workflow, catalog.views))
      commands.push({
        id: `view.open:${keyOf(view)}`,
        title: `Show as ${view.title}`,
        group: 'Views',
        run: () => state.setPanelView(active.id, keyOf(view)),
      });

  const contextContract = catalog.contracts.find((c) => c.contractId === CONTEXT_CONTRACT);
  for (const [field, schema] of Object.entries(contextContract?.schema.properties ?? {}))
    for (const option of schema.enum ?? [])
      commands.push({
        id: `context.set:${field}=${option}`,
        title: `Set ${schema.title ?? field} to ${option}`,
        group: 'Context',
        run: () => state.requestContext(contextContract!, { [field]: option }),
      });

  commands.push(
    {
      id: 'context.focus',
      title: 'Edit context',
      group: 'Context',
      shortcut: 'mod+shift+E',
      run: () => document.querySelector<HTMLElement>('#context-bar input')?.focus(),
    },
    {
      id: 'panel.focusGrid',
      title: 'Focus grid',
      group: 'Panels',
      shortcut: 'mod+shift+G',
      ...(active === undefined ? { unavailable: 'No panel is open.' } : {}),
      run: () => { if (!focusGrid(active?.id)) args.notify('This panel has no grid yet.'); },
    },
    {
      id: 'panel.next',
      title: 'Next panel',
      group: 'Panels',
      shortcut: 'mod+alt+ArrowRight',
      ...(state.panels.length < 2 ? { unavailable: 'There is no other panel.' } : {}),
      run: () => state.cycle(1),
    },
    {
      id: 'panel.previous',
      title: 'Previous panel',
      group: 'Panels',
      shortcut: 'mod+alt+ArrowLeft',
      ...(state.panels.length < 2 ? { unavailable: 'There is no other panel.' } : {}),
      run: () => state.cycle(-1),
    },
    {
      id: 'panel.close',
      title: 'Close panel',
      group: 'Panels',
      shortcut: 'mod+shift+X',
      ...(active === undefined ? { unavailable: 'No panel is open.' } : {}),
      run: () => { if (active !== undefined) state.closePanel(active.id); },
    },
    {
      id: 'theme.toggle',
      title: 'Toggle light / dark theme',
      group: 'Appearance',
      shortcut: 'mod+alt+T',
      run: args.toggleTheme,
    },
  );
  assertDistinct(commands);
  return commands;
}

export function CommandPalette() {
  const workspace = useWorkspaceContext();
  const panels = useWorkspace((s) => s.panels);
  const activePanelId = useWorkspace((s) => s.activePanelId);
  const context = useWorkspace((s) => s.context);
  const { toggleColorScheme } = useMantineColorScheme();
  const active = panels.find((p) => p.id === activePanelId);
  const runId = active?.kind === 'run' ? active.runId : undefined;
  // Observes the active Run's cached record; its panel is the one that fetches and polls it.
  const activeRun = useQuery<RunRecord>({
    queryKey: ['run', runId],
    queryFn: () => workspace.api.run(runId!),
    enabled: false,
  }).data;

  const commands = useMemo(() => {
    const state = workspace.store.getState();
    return buildCommands({ catalog: workspace.catalog, state: { ...state, panels, activePanelId, context }, activeRun, toggleTheme: toggleColorScheme, notify: workspace.notify });
  }, [workspace, panels, activePanelId, context, activeRun, toggleColorScheme]);

  const run = (command: Command) => {
    const reason = execute(command);
    if (reason !== undefined) workspace.notify(`${command.title}: ${reason}`);
  };

  // The query is held here and starts empty at every opening, rather than cleared when the closing
  // transition ends, which a surface opened by the command can cut short.
  const [query, setQuery] = useState('');
  const choose = (command: Command) => {
    letGo();
    setQuery('');
    run(command);
    // A command that opened a surface leaves the keyboard to it; any other gives it back.
    if (workspace.store.getState().runDialog === undefined && document.activeElement === document.body) returnKeyboard();
    else keyboardBefore = null;
  };

  useHotkeys([['mod+K', openPalette], ...commands.filter((c) => c.shortcut !== undefined).map((c): [string, () => void] => [c.shortcut!, () => run(c)])], []);

  const actions: SpotlightActionData[] = commands.map((c) => ({
    id: c.id,
    label: c.title,
    group: c.group,
    ...(c.unavailable === undefined ? {} : { description: c.unavailable }),
    ...(c.shortcut === undefined ? {} : { rightSection: <Kbd size="xs">{c.shortcut.replace('mod', 'Ctrl')}</Kbd> }),
    onClick: () => choose(c),
  }));

  // Ranked by title (rankCommands), then gathered into groups in the order their best command appears.
  const filter: SpotlightFilterFunction = (q) => {
    const byId = new Map(actions.map((a) => [a.id, a]));
    const groups = new Map<string, SpotlightActionGroupData>();
    for (const command of rankCommands(q, commands)) {
      const group = groups.get(command.group) ?? { group: command.group, actions: [] };
      groups.set(command.group, group);
      group.actions.push(byId.get(command.id)!);
    }
    return [...groups.values()];
  };

  return (
    <Spotlight
      filter={filter}
      actions={actions}
      shortcut={null}
      nothingFound="No command matches."
      highlightQuery
      limit={50}
      searchProps={{ placeholder: 'Type a command…', 'aria-label': 'Command' }}
      className="qwb-palette"
      returnFocus={false}
      query={query}
      onQueryChange={setQuery}
      onSpotlightOpen={() => setQuery('')}
      onSpotlightClose={returnKeyboard}
    />
  );
}

// Focus moves are owned here rather than left to Mantine's timers. Until a surface has mounted and
// taken the keyboard, keys go to nothing: opening the palette and choosing a command first let go of
// the focused element, so a key typed at machine speed is dropped, never delivered to the grid behind
// the palette or to a palette that is closing. The keyboard goes back where it was when the palette
// closes without opening a surface of its own.
let keyboardBefore: HTMLElement | null = null;

function letGo(): void {
  if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
}

function returnKeyboard(): void {
  const element = keyboardBefore;
  keyboardBefore = null;
  if (element !== null && element.isConnected) element.focus({ preventScroll: true });
}

export function openPalette(): void {
  if (document.activeElement?.closest('.qwb-palette') != null) return;
  if (keyboardBefore === null && document.activeElement instanceof HTMLElement && document.activeElement !== document.body)
    keyboardBefore = document.activeElement;
  letGo();
  spotlight.open();
}
