// Which panel's grid takes the keyboard when the "focus grid" command runs. Panels register the
// focus of their first grid; the command asks the active panel's.

const focusers = new Map<string, () => void>();

export function registerGridFocus(panelId: string, focus: (() => void) | undefined): void {
  if (focus === undefined) focusers.delete(panelId);
  else focusers.set(panelId, focus);
}

export function focusGrid(panelId: string | undefined): boolean {
  const focus = panelId === undefined ? undefined : focusers.get(panelId);
  if (focus === undefined) return false;
  focus();
  return true;
}
