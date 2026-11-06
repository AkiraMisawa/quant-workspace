import { ActionIcon, Button, Divider, Kbd, Title, Tooltip, useMantineColorScheme } from '@mantine/core';
import { IconMoon, IconSparkles, IconSun, IconTerminal2 } from '@tabler/icons-react';
import { useAi, useAiContext } from '../ai/AiProvider';
import { openPalette } from './CommandPalette';
import { ContextBar } from './ContextBar';

/**
 * The header's height on each breakpoint, which AppShell reserves above the main area. It is fixed, never `auto`:
 * AppShell offsets the main area by this number, so a header that grows past it covers the panel below
 * (docs/design-guidelines.md, "The shell"). One row of Control Height (30px), centred, from `md` up; below `md`,
 * two such rows 8px apart. Each number includes the header's 1px bottom border.
 */
export const HEADER_HEIGHT = { base: 82, md: 44 } as const;

/** The size of an icon inside a control of Control Height (docs/design-guidelines.md, "Icons"). */
const ICON = 16;

/**
 * Three zones on one row: what this is and how to command it, the context every run is bound to, and the switches
 * that change only the shell. Every control is one Control Height tall, so their edges line up with each other and
 * with the inputs' (docs/design-guidelines.md, "Alignment"). Below `md` the context moves to a second row of its own.
 */
export function AppHeader() {
  const { colorScheme, toggleColorScheme } = useMantineColorScheme();
  const aiOpen = useAi((s) => s.open);
  const ai = useAiContext().store;
  return (
    <div className="qwb-header">
      <div className="qwb-header-brand">
        <Title order={1} className="qwb-header-title">QUANT WORKBENCH</Title>
        <Button size="xs" variant="default" onClick={openPalette} aria-label="Open command line"
          leftSection={<IconTerminal2 size={ICON} stroke={1.75} aria-hidden />}
          rightSection={<Kbd size="xs" visibleFrom="sm">Ctrl K</Kbd>}>Commands</Button>
      </div>
      <ContextBar className="qwb-header-context" />
      <div className="qwb-header-actions">
        <Divider orientation="vertical" visibleFrom="md" className="qwb-header-divider" />
        <Button size="xs" variant={aiOpen ? 'light' : 'default'} onClick={() => ai.getState().setOpen(!aiOpen)}
          aria-pressed={aiOpen} aria-label={aiOpen ? 'Hide the AI pane' : 'Show the AI pane'}
          leftSection={<IconSparkles size={ICON} stroke={1.75} aria-hidden />}>AI</Button>
        <Tooltip label={colorScheme === 'dark' ? 'Light theme (toggle theme)' : 'Dark theme (toggle theme)'}>
          <ActionIcon size="input-xs" variant="default" onClick={toggleColorScheme} aria-label="Toggle theme">
            {colorScheme === 'dark' ? <IconSun size={ICON} stroke={1.75} aria-hidden /> : <IconMoon size={ICON} stroke={1.75} aria-hidden />}
          </ActionIcon>
        </Tooltip>
      </div>
    </div>
  );
}
