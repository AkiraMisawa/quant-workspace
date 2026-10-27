// What an open XVA workspace page offers to commands: its Price, by panel. The page registers it while it
// is mounted, so the command and the button run the same function.

const prices = new Map<string, () => void>();

export function registerPrice(panelId: string, price: (() => void) | undefined): void {
  if (price === undefined) prices.delete(panelId);
  else prices.set(panelId, price);
}

export function priceOf(panelId: string | undefined): (() => void) | undefined {
  return panelId === undefined ? undefined : prices.get(panelId);
}
