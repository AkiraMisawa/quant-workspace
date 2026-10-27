// One command path for the palette, keyboard shortcuts and buttons: each surface looks commands up
// here and runs them, so availability and behaviour cannot differ between surfaces.

export interface Command {
  readonly id: string;
  readonly title: string;
  readonly group: string;
  readonly keywords?: readonly string[];
  /** A shortcut in Mantine's notation, e.g. `mod+shift+R`. Shown in the palette and bound globally. */
  readonly shortcut?: string;
  /** Why the command cannot run now; absent when it can. */
  readonly unavailable?: string;
  readonly run: () => void;
}

/** Runs a command if it is available. Returns the reason when it is not, so a caller can show it. */
export function execute(command: Command): string | undefined {
  if (command.unavailable !== undefined) return command.unavailable;
  command.run();
  return undefined;
}

/** Commands whose ids collide would make a shortcut or a palette entry ambiguous; refuse them. */
export function assertDistinct(commands: readonly Command[]): void {
  const seen = new Set<string>();
  for (const command of commands) {
    if (seen.has(command.id)) throw new Error(`Command ${command.id} is defined twice.`);
    seen.add(command.id);
  }
  const shortcuts = new Map<string, string>();
  for (const command of commands) {
    if (command.shortcut === undefined) continue;
    const other = shortcuts.get(command.shortcut.toLowerCase());
    if (other !== undefined) throw new Error(`Shortcut ${command.shortcut} is bound to both ${other} and ${command.id}.`);
    shortcuts.set(command.shortcut.toLowerCase(), command.id);
  }
}

/**
 * The commands a palette query matches, best first: the exact title, then titles that start with the
 * query, then titles that contain it, then commands whose title, group and keywords hold every word. Enter
 * runs the first, so a title typed in full always runs that command and never a longer one.
 */
export function rankCommands(query: string, commands: readonly Command[]): Command[] {
  const q = query.trim().toLowerCase();
  if (q === '') return [...commands];
  const words = q.split(/\s+/);
  const rank = (command: Command): number => {
    const title = command.title.toLowerCase();
    if (title === q) return 0;
    if (title.startsWith(q)) return 1;
    if (title.includes(q)) return 2;
    const haystack = [title, command.group.toLowerCase(), ...(command.keywords ?? []).map((k) => k.toLowerCase())].join(' ');
    return words.every((w) => haystack.includes(w)) ? 3 : -1;
  };
  return commands
    .map((command, index) => ({ command, index, rank: rank(command) }))
    .filter((r) => r.rank >= 0)
    .sort((a, b) => a.rank - b.rank || a.index - b.index)
    .map((r) => r.command);
}
