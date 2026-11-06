import type { ProductDescriptor } from '../../platform/products';
import type { RatesWorkspaceView } from './contracts';

// What an open Rates page offers to commands, by panel: Price, live, Capture and new lines, and the
// products the server describes, for the command line's completions (ADR-0006). The page registers them while it is mounted, so a
// command and a button run the same function.

export interface RatesCommands {
  readonly price: () => void;
  readonly startLive: () => void;
  readonly stopLive: () => void;
  readonly capture: () => void;
  readonly addLine: (product: 'irs' | 'ccs') => void;
  /** `new <product> <terms>` (ADR-0008): one line from the words typed, read by the server, through the page's edit queue. */
  readonly newLine: (product: string, terms: string) => void;
  /** Sets the workspace's defaults for new lines, through the page's edit queue: those named change, a null clears one. */
  readonly setDefaults: (defaults: Partial<RatesWorkspaceView['defaults']>) => void;
  /** The products the server describes for Rates; empty until they have loaded. */
  readonly products: () => readonly ProductDescriptor[];
}

const pages = new Map<string, RatesCommands>();

export function registerRatesCommands(panelId: string, commands: RatesCommands | undefined): void {
  if (commands === undefined) pages.delete(panelId);
  else pages.set(panelId, commands);
}

export function ratesCommandsOf(panelId: string | undefined): RatesCommands | undefined {
  return panelId === undefined ? undefined : pages.get(panelId);
}
