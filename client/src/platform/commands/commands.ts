// One command path for the command line, the shell's few keys and buttons (ADR-0006): each surface looks commands up
// here and runs them, so availability and behaviour cannot differ between surfaces. A command is found by typing the
// English words for what it does; its arguments are completed one slot at a time.

/** A value a slot offers, with what it means. */
export interface Completion {
  readonly value: string;
  readonly detail?: string;
}

export interface CommandArgument {
  /** The slot's name, shown in the line (`currency`); an optional one is typed `name=value`. */
  readonly name: string;
  /** Required arguments are typed in order; optional ones by name, anywhere after the command. */
  readonly required: boolean;
  /** What the slot takes, shown while it is typed (`a tenor such as 10y, or a date`). */
  readonly hint?: string;
  /** Takes the rest of the line as typed, spaces included (the request to the AI). Only a last required argument. */
  readonly rest?: boolean;
  /** The values this slot offers, given the arguments before it. The line keeps those that match what is typed. */
  readonly complete?: (given: Readonly<Record<string, string>>) => readonly Completion[];
  /** Why a value is refused; undefined when the slot takes it. */
  readonly check?: (value: string, given: Readonly<Record<string, string>>) => string | undefined;
}

export interface Command {
  readonly id: string;
  /** The English words that name it, lower case: a verb and its object (`price`, `new irs`, `live start`). */
  readonly words: string;
  /** What it does, in a sentence's words. */
  readonly title: string;
  readonly group: string;
  /** What it acts on now: the active panel by default (ADR-0006 §5). */
  readonly target?: string;
  readonly args?: readonly CommandArgument[];
  /** A shell chord in Mantine's notation (`mod+shift+G`). Only the shell's commands carry one (ADR-0006 §8). */
  readonly shortcut?: string;
  /** Why the command cannot run now; absent when it can. */
  readonly unavailable?: string;
  readonly run: (args: Readonly<Record<string, string>>) => void;
}

/** Runs a command if it is available. Returns the reason when it is not, so a caller can show it. */
export function execute(command: Command, args: Readonly<Record<string, string>> = {}): string | undefined {
  if (command.unavailable !== undefined) return command.unavailable;
  command.run(args);
  return undefined;
}

/** Commands whose ids, words or chords collide would make the line or a key ambiguous; refuse them. */
export function assertDistinct(commands: readonly Command[]): void {
  for (const [what, key] of [['Command', (c: Command) => c.id], ['The words', (c: Command) => c.words]] as const) {
    const seen = new Map<string, string>();
    for (const command of commands) {
      const k = key(command);
      const other = seen.get(k);
      if (other !== undefined) throw new Error(`${what} ${k} name both ${other} and ${command.id}.`);
      seen.set(k, command.id);
    }
  }
  const shortcuts = new Map<string, string>();
  for (const command of commands) {
    if (command.shortcut === undefined) continue;
    const other = shortcuts.get(command.shortcut.toLowerCase());
    if (other !== undefined) throw new Error(`Shortcut ${command.shortcut} is bound to both ${other} and ${command.id}.`);
    shortcuts.set(command.shortcut.toLowerCase(), command.id);
  }
}

/** How often and how lately the user ran a command: higher first among equal matches (ADR-0006 §6). */
export type UsageScore = (commandId: string) => number;

/**
 * The commands a query matches, best first. The words typed in full come first, then words that start with the
 * query, then commands whose words each start with the query's words in order (`op pr` → `open pricing db`), then
 * those whose words or title contain the query. Within a tier, what the user runs most comes first, then what can
 * run now. `?` lists every command that can run now.
 */
export function rankCommands(query: string, commands: readonly Command[], usage: UsageScore = () => 0): Command[] {
  const q = query.trim().toLowerCase().replace(/\s+/g, ' ');
  if (q === '') return order(commands.map((command, index) => ({ command, index, tier: 0 })), usage);
  if (q === '?') return order(commands.filter((c) => c.unavailable === undefined).map((command, index) => ({ command, index, tier: 0 })), usage);
  const typed = q.split(' ');
  const tier = (command: Command): number => {
    if (command.words === q) return 0;
    if (command.words.startsWith(q)) return 1;
    if (inOrder(typed, command.words.split(' '))) return 2;
    const haystack = `${command.words} ${command.title.toLowerCase()} ${command.group.toLowerCase()}`;
    return typed.every((t) => haystack.includes(t)) ? 3 : -1;
  };
  return order(commands.map((command, index) => ({ command, index, tier: tier(command) })).filter((r) => r.tier >= 0), usage);
}

function inOrder(typed: readonly string[], words: readonly string[]): boolean {
  let at = 0;
  for (const t of typed) {
    while (at < words.length && !words[at]!.startsWith(t)) at++;
    if (at === words.length) return false;
    at++;
  }
  return true;
}

function order(rows: { command: Command; index: number; tier: number }[], usage: UsageScore): Command[] {
  return rows
    .sort((a, b) => a.tier - b.tier
      || usage(b.command.id) - usage(a.command.id)
      || Number(a.command.unavailable !== undefined) - Number(b.command.unavailable !== undefined)
      || a.index - b.index)
    .map((r) => r.command);
}

/** What the line holds: a command still being chosen, or a chosen command and its arguments so far. */
export type LineReading =
  | { readonly kind: 'choosing'; readonly query: string }
  | {
    readonly kind: 'arguments';
    readonly command: Command;
    /** The arguments typed in full, by name. */
    readonly given: Readonly<Record<string, string>>;
    /** The slot being typed now, and what is typed in it so far; undefined once every slot is filled. */
    readonly slot: CommandArgument | undefined;
    readonly typed: string;
    /** Values the line cannot place: more than the command takes, or a name it does not have. */
    readonly problems: readonly string[];
  };

/**
 * Reads the line. A command is chosen once its words are typed in full and followed by a space; what follows is its
 * arguments: required ones in order, optional ones as `name=value`.
 */
export function readLine(text: string, commands: readonly Command[]): LineReading {
  const lead = text.replace(/^\s+/, '');
  const tokens = lead.split(/\s+/).filter((t) => t !== '');
  const lower = tokens.map((t) => t.toLowerCase());
  const closed = /\s$/.test(lead);
  let chosen: { command: Command; length: number } | undefined;
  for (const command of commands) {
    const words = command.words.split(' ');
    if (words.length > lower.length || (words.length === lower.length && !closed)) continue;
    if (words.every((w, i) => lower[i] === w) && (chosen === undefined || words.length > chosen.length)) chosen = { command, length: words.length };
  }
  if (chosen === undefined) return { kind: 'choosing', query: lead };

  const { command } = chosen;
  const args = command.args ?? [];
  const rest = tokens.slice(chosen.length);
  const done = closed ? rest : rest.slice(0, -1);
  const typedNow = closed ? '' : rest.at(-1) ?? '';
  const given: Record<string, string> = {};
  const problems: string[] = [];
  const required = args.filter((a) => a.required);
  let next = 0;

  const restArg = required.find((a) => a.rest);
  if (restArg !== undefined) {
    // Everything after the words, as typed: a request to the AI keeps its spaces.
    const after = lead.slice(lead.toLowerCase().indexOf(command.words) + command.words.length).replace(/^\s+/, '');
    return { kind: 'arguments', command, given: after === '' ? {} : { [restArg.name]: after }, slot: restArg, typed: after, problems: [] };
  }

  const place = (token: string): void => {
    const eq = token.indexOf('=');
    if (eq > 0) {
      const name = token.slice(0, eq).toLowerCase();
      const arg = args.find((a) => !a.required && a.name === name);
      if (arg === undefined) problems.push(`${command.words} has no option ${name}.`);
      else given[arg.name] = token.slice(eq + 1);
      return;
    }
    const arg = required[next++];
    if (arg === undefined) problems.push(`${command.words} takes no more values: ${token}.`);
    else given[arg.name] = token;
  };
  for (const token of done) place(token);

  let slot: CommandArgument | undefined;
  let typed = typedNow;
  const eq = typedNow.indexOf('=');
  if (eq > 0) {
    slot = args.find((a) => !a.required && a.name === typedNow.slice(0, eq).toLowerCase());
    typed = typedNow.slice(eq + 1);
  } else slot = required[next];
  return { kind: 'arguments', command, given, slot, typed, problems };
}

/** The completions a reading offers: commands while choosing, the slot's values once a command is chosen. */
export function completionsOf(reading: LineReading, commands: readonly Command[], usage: UsageScore = () => 0):
  { readonly commands: readonly Command[]; readonly values: readonly Completion[] } {
  if (reading.kind === 'choosing') return { commands: rankCommands(reading.query, commands, usage), values: [] };
  const { command, given, slot, typed } = reading;
  const options = slot === undefined
    // Every required slot is filled: offer the options not given yet.
    ? (command.args ?? []).filter((a) => !a.required && given[a.name] === undefined).map((a) => ({ value: `${a.name}=`, ...(a.hint === undefined ? {} : { detail: a.hint }) }))
    : slot.complete?.(given) ?? [];
  const t = typed.toLowerCase();
  const starts = options.filter((o) => o.value.toLowerCase().startsWith(t));
  const contains = options.filter((o) => !o.value.toLowerCase().startsWith(t) && (o.value.toLowerCase().includes(t) || (o.detail ?? '').toLowerCase().includes(t)));
  return { commands: [], values: [...starts, ...contains] };
}

/** What Enter would run, or every reason it would not: missing arguments by name, refused values, unplaced ones. */
export function finish(reading: Extract<LineReading, { kind: 'arguments' }>): { readonly args: Readonly<Record<string, string>> } | { readonly problems: readonly string[] } {
  const { command, slot, typed } = reading;
  const args: Record<string, string> = { ...reading.given };
  if (slot !== undefined && typed !== '') args[slot.name] = typed;
  const problems = [...reading.problems];
  const missing = (command.args ?? []).filter((a) => a.required && (args[a.name] ?? '') === '').map((a) => a.name);
  if (missing.length > 0) problems.push(`Missing: ${missing.join(', ')}.`);
  for (const arg of command.args ?? []) {
    const value = args[arg.name];
    if (value === undefined || value === '') continue;
    const refused = arg.check?.(value, args);
    if (refused !== undefined) problems.push(`${arg.name}: ${refused}`);
  }
  return problems.length > 0 ? { problems } : { args };
}

/** The line once a completion is taken: the command's words, or the slot's value, then a space for the next slot. */
export function take(text: string, reading: LineReading, completion: Command | Completion): string {
  if ('words' in completion) return `${completion.words} `;
  if (reading.kind === 'choosing') return text;
  const keep = text.replace(/\S*$/, '');
  const named = reading.slot !== undefined && !reading.slot.required ? `${reading.slot.name}=` : '';
  return completion.value.endsWith('=') ? `${keep}${completion.value}` : `${keep}${named}${completion.value} `;
}

/** A command's arguments as the line shows them: `<currency> <index> [rate=]`. */
export function signature(command: Command): string {
  return (command.args ?? []).map((a) => (a.required ? `<${a.name}>` : `[${a.name}=]`)).join(' ');
}
