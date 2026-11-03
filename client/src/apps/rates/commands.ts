// What an open Rates page offers to commands, by panel: Price, live, Capture and new lines, and the conventions its
// legs may name, for the command line's completions (ADR-0006). The page registers them while it is mounted, so a
// command and a button run the same function.

export interface RatesCommands {
  readonly price: () => void;
  readonly startLive: () => void;
  readonly stopLive: () => void;
  readonly capture: () => void;
  readonly addLine: (product: 'irs' | 'ccs') => void;
  /** Creates one line from its terms, by the grid's own field names, through the page's edit queue. */
  readonly createLine: (terms: Readonly<Record<string, unknown>>) => void;
  /** The confirmed currency and index pairs, as the server lists them; empty until they have loaded. */
  readonly conventions: () => readonly { readonly currency: string; readonly index: string }[];
}

const pages = new Map<string, RatesCommands>();

export function registerRatesCommands(panelId: string, commands: RatesCommands | undefined): void {
  if (commands === undefined) pages.delete(panelId);
  else pages.set(panelId, commands);
}

export function ratesCommandsOf(panelId: string | undefined): RatesCommands | undefined {
  return panelId === undefined ? undefined : pages.get(panelId);
}
