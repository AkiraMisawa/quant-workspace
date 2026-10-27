import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { Badge, Group, Kbd, Modal, Stack, Text, TextInput, UnstyledButton, useMantineColorScheme } from '@mantine/core';
import { useHotkeys } from '@mantine/hooks';
import {
  assertDistinct, completionsOf, execute, finish, readLine, signature, take,
  type Command, type Completion,
} from '../platform/commands/commands';
import { recordUse, usageScore } from '../platform/commands/usage';
import { useQuery } from '@tanstack/react-query';
import type { Catalog, RunRecord } from '../platform/contracts';
import { viewsFor } from '../platform/views/binding';
import { CONTEXT_CONTRACT, useWorkspace, useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import { keyOf, type Panel, type WorkspaceState } from '../platform/workspace/workspaceStore';
import { focusGrid } from './focus';
import { appCommands } from '../apps';

/**
 * Builds every command the workbench offers right now. Workflows, views and context values come from
 * the catalog, so a module that registers a workflow gets its command without a client change.
 */
export function buildCommands(args: {
  readonly catalog: Catalog;
  readonly state: Pick<WorkspaceState, 'panels' | 'activePanelId' | 'context' | 'openRunDialog' | 'setPanelView' | 'closePanel' | 'cycle' | 'requestContext' | 'showApp' | 'openAppDialog'>;
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
      words: `run ${wordsOf(workflow.title)}`,
      title: `Run ${workflow.title}`,
      group: 'Workflows',
      ...(state.context.prepared === undefined ? { unavailable: 'The context is not valid.' } : {}),
      run: () => state.openRunDialog(keyOf(workflow)),
    });

  if (active?.kind === 'run' && activeRun !== undefined && activeRun.status !== 'running')
    for (const view of viewsFor(activeRun.workflow, catalog.views))
      commands.push({
        id: `view.open:${keyOf(view)}`,
        words: `show ${wordsOf(view.title)}`,
        title: `Show as ${view.title}`,
        group: 'Views',
        target: active.title,
        run: () => state.setPanelView(active.id, keyOf(view)),
      });

  const contextContract = catalog.contracts.find((c) => c.contractId === CONTEXT_CONTRACT);
  for (const [field, schema] of Object.entries(contextContract?.schema.properties ?? {})) {
    const options = schema.enum ?? [];
    if (options.length === 0) continue;
    const name = schema.title ?? field;
    commands.push({
      id: `context.set:${field}`,
      words: `set ${wordsOf(name)}`,
      title: `Set ${name}`,
      group: 'Context',
      args: [{
        name: wordsOf(name).replace(/ /g, '-'),
        required: true,
        complete: () => options.map((value) => ({ value })),
        check: (value) => (options.some((o) => o.toLowerCase() === value.toLowerCase()) ? undefined : `one of ${options.join(', ')}`),
      }],
      run: (given) => {
        const value = Object.values(given)[0]!;
        state.requestContext(contextContract!, { [field]: options.find((o) => o.toLowerCase() === value.toLowerCase())! });
      },
    });
  }

  commands.push(
    {
      id: 'context.focus',
      words: 'edit context',
      title: 'Edit the context',
      group: 'Context',
      shortcut: 'mod+shift+E',
      run: () => document.querySelector<HTMLElement>('#context-bar input')?.focus(),
    },
    {
      id: 'panel.focusGrid',
      words: 'focus grid',
      title: 'Give the keyboard to the panel\'s grid',
      group: 'Panels',
      shortcut: 'mod+shift+G',
      ...(active === undefined ? { unavailable: 'No panel is open.' } : { target: active.title }),
      run: () => { if (!focusGrid(active?.id)) args.notify('This panel has no grid yet.'); },
    },
    {
      id: 'panel.next',
      words: 'next panel',
      title: 'Show the next panel',
      group: 'Panels',
      shortcut: 'mod+alt+ArrowRight',
      ...(state.panels.length < 2 ? { unavailable: 'There is no other panel.' } : {}),
      run: () => state.cycle(1),
    },
    {
      id: 'panel.previous',
      words: 'previous panel',
      title: 'Show the previous panel',
      group: 'Panels',
      shortcut: 'mod+alt+ArrowLeft',
      ...(state.panels.length < 2 ? { unavailable: 'There is no other panel.' } : {}),
      run: () => state.cycle(-1),
    },
    {
      id: 'panel.close',
      words: 'close panel',
      title: 'Close the panel',
      group: 'Panels',
      shortcut: 'mod+shift+X',
      ...(active === undefined ? { unavailable: 'No panel is open.' } : { target: active.title }),
      run: () => { if (active !== undefined) state.closePanel(active.id); },
    },
    {
      id: 'theme.toggle',
      words: 'toggle theme',
      title: 'Switch between light and dark',
      group: 'Appearance',
      run: args.toggleTheme,
    },
    {
      // ADR-0006 §7: the request goes to the AI pane with the current context. No runtime is connected yet (C34), so
      // the command says so and sends nothing.
      id: 'ai.ask',
      words: 'ai',
      title: 'Ask the AI',
      group: 'AI',
      args: [{ name: 'request', required: true, rest: true, hint: 'what you want done, in your own words' }],
      unavailable: 'No AI runtime is connected yet; nothing was sent.',
      ...(active === undefined ? {} : { target: active.title }),
      run: () => undefined,
    },
    ...appCommands(state),
  );
  assertDistinct(commands);
  return commands;
}

/** A title as the words a command is typed with: lower case, single spaces, no punctuation. */
export function wordsOf(title: string): string {
  return title.toLowerCase().replace(/[^a-z0-9.\-/ ]+/g, ' ').replace(/\s+/g, ' ').trim();
}

let openLine: (() => void) | undefined;

/** Opens the command line (Ctrl+K anywhere; `/` where it would not be typed into something). */
export function openPalette(): void {
  if (document.activeElement?.closest('.qwb-palette') != null) return;
  if (keyboardBefore === null && document.activeElement instanceof HTMLElement && document.activeElement !== document.body)
    keyboardBefore = document.activeElement;
  letGo();
  openLine?.();
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

  const [opened, setOpened] = useState(false);
  // The text is held here and starts empty at every opening.
  const [text, setText] = useState('');
  const [highlight, setHighlight] = useState(0);
  const [problems, setProblems] = useState<readonly string[]>([]);
  const input = useRef<HTMLInputElement>(null);

  // Built when the line opens and as the panels change, so a page that registers its commands after mounting is seen.
  const commands = useMemo(() => {
    const state = workspace.store.getState();
    return buildCommands({ catalog: workspace.catalog, state: { ...state, panels, activePanelId, context }, activeRun, toggleTheme: toggleColorScheme, notify: workspace.notify });
  }, [workspace, panels, activePanelId, context, activeRun, toggleColorScheme, opened]);

  useEffect(() => {
    openLine = () => { setText(''); setProblems([]); setHighlight(0); setOpened(true); };
    return () => { openLine = undefined; };
  }, []);

  const run = (command: Command, args: Readonly<Record<string, string>> = {}) => {
    const reason = execute(command, args);
    if (reason !== undefined) workspace.notify(`${command.words}: ${reason}`);
    else recordUse(command.id);
  };

  useHotkeys([
    ['mod+K', openPalette],
    ...commands.filter((c) => c.shortcut !== undefined).map((c): [string, () => void] => [c.shortcut!, () => run(c)]),
  ], []);

  // `/` opens the line wherever it would not be typed into something: not in a text field, not in a grid, where it
  // starts editing a cell, and not in the AI chat, whose slash commands are its own (ADR-0006 §2).
  useEffect(() => {
    const onKey = (event: globalThis.KeyboardEvent) => {
      if (event.key !== '/' || event.ctrlKey || event.metaKey || event.altKey || event.defaultPrevented) return;
      const at = event.target instanceof Element ? event.target : null;
      if (at?.closest('input, textarea, select, [contenteditable=""], [contenteditable="true"], .ex-grid, [data-ai-chat]') != null) return;
      event.preventDefault();
      openPalette();
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, []);

  const reading = readLine(text, commands);
  const offered = completionsOf(reading, commands, usageScore);
  const items: readonly (Command | Completion)[] = reading.kind === 'choosing' ? offered.commands : offered.values;
  const at = Math.min(highlight, Math.max(items.length - 1, 0));
  const chosen = items[at];

  const change = (next: string) => { setText(next); setHighlight(0); setProblems([]); };
  const close = () => { setOpened(false); returnKeyboard(); };
  const runNow = (command: Command, args: Readonly<Record<string, string>>) => {
    letGo();
    setOpened(false);
    run(command, args);
    // A command that opened a surface leaves the keyboard to it; any other gives it back.
    if (workspace.store.getState().runDialog === undefined && document.activeElement === document.body) returnKeyboard();
    else keyboardBefore = null;
  };

  const takeItem = (item: Command | Completion) => {
    change(take(text, reading, item));
    input.current?.focus();
  };

  const enter = () => {
    if (reading.kind === 'choosing') {
      if (!(chosen !== undefined && 'words' in chosen)) { setProblems([text.trim() === '' ? 'Type what you want to do.' : 'No command matches.']); return; }
      if (chosen.unavailable !== undefined) { setProblems([chosen.unavailable]); return; }
      if ((chosen.args ?? []).some((a) => a.required)) { takeItem(chosen); return; }
      runNow(chosen, {});
      return;
    }
    if (reading.command.unavailable !== undefined) { setProblems([reading.command.unavailable]); return; }
    const done = finish(reading);
    if ('problems' in done) { setProblems(done.problems); return; }
    runNow(reading.command, done.args);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      if (items.length > 0) setHighlight((at + (event.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length);
    } else if (event.key === 'Tab') {
      event.preventDefault();
      if (chosen !== undefined) takeItem(chosen);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      enter();
    }
  };

  const optionId = (i: number) => `qwb-line-option-${i}`;
  const head = reading.kind === 'arguments' ? reading.command : chosen !== undefined && 'words' in chosen ? chosen : undefined;

  return (
    <Modal opened={opened} onClose={close} withCloseButton={false} trapFocus returnFocus={false} size="lg" yOffset="10vh"
      transitionProps={{ duration: 0 }} className="qwb-palette" aria-label="Command line" padding="sm">
      <Stack gap={6}>
        <TextInput ref={input} data-autofocus value={text} onChange={(e) => change(e.currentTarget.value)} onKeyDown={onKeyDown}
          placeholder="Type what you want to do: price, new irs, live start… (? lists them)" aria-label="Command"
          role="combobox" aria-expanded={items.length > 0} aria-controls="qwb-line-options" aria-autocomplete="list"
          {...(chosen === undefined ? {} : { 'aria-activedescendant': optionId(at) })}
          styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }} />
        {head !== undefined && <Head command={head} reading={reading} />}
        {problems.length > 0 && (
          <Stack gap={0} role="alert" data-testid="command-problems">
            {problems.map((p) => <Text key={p} size="xs" c="red">{p}</Text>)}
          </Stack>
        )}
        <Stack gap={0} id="qwb-line-options" role="listbox" aria-label={reading.kind === 'choosing' ? 'Commands' : `Values for ${reading.slot?.name ?? 'options'}`}
          style={{ maxHeight: '50vh', overflowY: 'auto' }}>
          {items.slice(0, 50).map((item, i) => (
            <UnstyledButton key={'words' in item ? item.id : item.value} id={optionId(i)} role="option" aria-selected={i === at} tabIndex={-1}
              className="qwb-line-option" data-selected={i === at || undefined} onMouseDown={(e) => e.preventDefault()}
              onClick={() => ('words' in item && !(item.args ?? []).some((a) => a.required) && item.unavailable === undefined ? runNow(item, {}) : takeItem(item))}
              px="xs" py={4} style={{ borderRadius: 4, background: i === at ? 'var(--mantine-color-default-hover)' : undefined }}>
              {'words' in item ? <CommandOption command={item} /> : <ValueOption completion={item} />}
            </UnstyledButton>
          ))}
          {items.length === 0 && reading.kind === 'choosing' && text.trim() !== '' && (
            <Text size="xs" c="dimmed" px="xs">No command matches. <Kbd size="xs">ai {text.trim()}</Kbd> asks the AI.</Text>
          )}
        </Stack>
        <Group gap="md">
          <Text size="xs" c="dimmed"><Kbd size="xs">Tab</Kbd> complete</Text>
          <Text size="xs" c="dimmed"><Kbd size="xs">Enter</Kbd> run</Text>
          <Text size="xs" c="dimmed"><Kbd size="xs">↑</Kbd><Kbd size="xs">↓</Kbd> choose</Text>
          <Text size="xs" c="dimmed"><Kbd size="xs">Esc</Kbd> close</Text>
        </Group>
      </Stack>
    </Modal>
  );
}

/** The chosen command with its slots: given values, the slot being typed, and those still to come. */
function Head({ command, reading }: { readonly command: Command; readonly reading: ReturnType<typeof readLine> }): ReactNode {
  const args = command.args ?? [];
  return (
    <Group gap={6} wrap="wrap" data-testid="command-pending">
      <Text size="sm" fw={600} ff="monospace">{command.words}</Text>
      {args.map((a) => {
        const value = reading.kind === 'arguments' ? reading.given[a.name] ?? (reading.slot === a && reading.typed !== '' ? reading.typed : undefined) : undefined;
        const current = reading.kind === 'arguments' && reading.slot === a;
        return (
          <Badge key={a.name} size="sm" radius="sm" variant={current ? 'filled' : value === undefined ? 'outline' : 'light'}
            color={value === undefined && a.required && !current ? 'gray' : 'blue'} style={{ textTransform: 'none' }}>
            {a.required ? '' : `${a.name}=`}{value ?? (a.required ? a.name : '…')}
          </Badge>
        );
      })}
      {command.target !== undefined && <Text size="xs" c="dimmed">on {command.target}</Text>}
      {reading.kind === 'arguments' && reading.slot?.hint !== undefined && <Text size="xs" c="dimmed">{reading.slot.name}: {reading.slot.hint}</Text>}
    </Group>
  );
}

function CommandOption({ command }: { readonly command: Command }): ReactNode {
  return (
    <Group gap="xs" wrap="nowrap" justify="space-between">
      <Group gap="xs" wrap="wrap">
        <Text size="sm" ff="monospace" fw={600} {...(command.unavailable === undefined ? {} : { c: 'dimmed' })}>{command.words}</Text>
        {signature(command) !== '' && <Text size="xs" ff="monospace" c="dimmed">{signature(command)}</Text>}
        <Text size="xs" c="dimmed">{command.title}{command.target === undefined ? '' : ` · ${command.target}`}</Text>
        {command.unavailable !== undefined && <Text size="xs" c="orange">{command.unavailable}</Text>}
      </Group>
      {command.shortcut !== undefined && <Kbd size="xs">{command.shortcut.replace('mod', 'Ctrl')}</Kbd>}
    </Group>
  );
}

function ValueOption({ completion }: { readonly completion: Completion }): ReactNode {
  return (
    <Group gap="xs" wrap="nowrap">
      <Text size="sm" ff="monospace">{completion.value}</Text>
      {completion.detail !== undefined && <Text size="xs" c="dimmed">{completion.detail}</Text>}
    </Group>
  );
}

// Focus moves are owned here rather than left to Mantine's timers. Until a surface has mounted and
// taken the keyboard, keys go to nothing: opening the line and running a command first let go of
// the focused element, so a key typed at machine speed is dropped, never delivered to the grid behind
// the line or to a line that is closing. The keyboard goes back where it was when the line
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
