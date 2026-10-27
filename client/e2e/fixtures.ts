import { test as base, expect, type Page } from '@playwright/test';

// An unexpected console message or page error fails the test that caused it.
export const test = base.extend<{ problems: string[] }>({
  problems: [async ({ page }, use) => {
    const problems: string[] = [];
    page.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') problems.push(`${m.type()}: ${m.text()}`); });
    page.on('pageerror', (e) => problems.push(`pageerror: ${e.message}`));
    await use(problems);
    expect(problems).toEqual([]);
  }, { auto: true }],
});
export { expect };

export async function open(page: Page): Promise<void> {
  await page.goto('/');
  await expect(page.getByText('Nothing open yet.')).toBeVisible();
}

/** Runs a palette command by typing its title, once the palette holds the keyboard. */
export async function command(page: Page, title: string): Promise<void> {
  await page.keyboard.press('ControlOrMeta+K');
  await expect(page.getByRole('textbox', { name: 'Command' })).toBeFocused();
  await page.keyboard.type(title);
  // Enter runs the first action the palette lists, so the test reads which one that is first.
  await expect(page.locator('.mantine-Spotlight-action').first()).toContainText(title);
  await page.keyboard.press('Enter');
}

export const panel = (page: Page) => page.getByRole('tabpanel');

/**
 * Awaits ExGrid's Settled Mark on the grid with this label. A key sent before a newly drawn grid has
 * settled is lost, by ExGrid's own contract (ex-grid tests/ExGrid.Browser/README.md, "Before the first
 * key of each page"), so a test waits for it before the first key and before it reads the grid.
 */
export async function settled(page: Page, label: string): Promise<void> {
  await expect(page.getByLabel(label, { exact: true }).locator('.ex-grid')).toHaveAttribute('data-ex-settled', '');
}
