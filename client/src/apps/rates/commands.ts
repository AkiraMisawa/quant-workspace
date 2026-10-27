// What an open Rates page offers to commands, by panel: its Price, live toggle and Capture. The page registers
// them while it is mounted, so a command and a button run the same function.

export interface RatesCommands {
  readonly price: () => void;
  readonly live: () => void;
  readonly capture: () => void;
}

const pages = new Map<string, RatesCommands>();

export function registerRatesCommands(panelId: string, commands: RatesCommands | undefined): void {
  if (commands === undefined) pages.delete(panelId);
  else pages.set(panelId, commands);
}

export function ratesCommandsOf(panelId: string | undefined): RatesCommands | undefined {
  return panelId === undefined ? undefined : pages.get(panelId);
}
