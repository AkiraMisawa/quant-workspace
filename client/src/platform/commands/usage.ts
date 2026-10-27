// How often and how lately this user ran each command, so that what they use comes first and a few letters find it
// (ADR-0006 §6). Kept in this browser until the user settings store arrives with sign-in (C30); a browser that keeps
// nothing ranks by match alone.

const KEY = 'qwb.commandUsage.v1';
const HALF_LIFE_DAYS = 14;

interface Use { readonly count: number; readonly last: number }

function load(): Record<string, Use> {
  try {
    const text = localStorage.getItem(KEY);
    return text === null ? {} : (JSON.parse(text) as Record<string, Use>);
  } catch {
    // Storage refused (a private window, blocked site data): ranking by match alone, as documented above.
    return {};
  }
}

let uses = load();

/** A command's score: its runs, each worth less the older it is. */
export function usageScore(commandId: string, now = Date.now()): number {
  const use = uses[commandId];
  if (use === undefined) return 0;
  return use.count * 0.5 ** ((now - use.last) / (HALF_LIFE_DAYS * 86_400_000));
}

export function recordUse(commandId: string, now = Date.now()): void {
  const before = uses[commandId];
  uses = { ...uses, [commandId]: { count: (before?.count ?? 0) + 1, last: now } };
  try {
    localStorage.setItem(KEY, JSON.stringify(uses));
  } catch {
    // Storage refused: the score holds for this page only.
  }
}

/** For tests. */
export function resetUsage(): void {
  uses = {};
}
