import type { Page } from '@playwright/test';
import { command, expect, open, settled, test } from './fixtures';

// Slice 03 (docs/plans/slice-03-rates-line-pricer.md): a line typed by keyboard, priced live, captured and
// priced once. The host runs with the development settings: the unconfirmed convention proposal and a synthetic
// market, every figure simulated.

async function newWorkspace(page: Page): Promise<void> {
  await command(page, 'New Rates workspace');
  const dialog = page.getByRole('dialog', { name: 'New Rates workspace' });
  await expect(dialog.getByLabel('Valuation date')).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('tab', { name: 'Rates 2026-10-27' })).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'Lines');
}

const lines = (page: Page) => page.getByLabel('Lines', { exact: true });
const firstLine = (page: Page) => lines(page).getByRole('row', { name: /^L\d+ / }).first();

/** A cell of a row by its column's position, 0 from the left: off-screen columns are not in the DOM, so never by count. */
const cell = (page: Page, column: number) => firstLine(page).locator(`[role=gridcell][aria-colindex="${column + 1}"]`);

const STATUS = 2;
const PV = 3;
const END = 8;
const LEG1_NOTIONAL = 12;
const LEG2_QUOTE = 22;

test('C40, docs/07: a line typed by keyboard ticks live, is captured and priced once', async ({ page }) => {
  await open(page);
  await newWorkspace(page);
  await page.getByRole('button', { name: 'Add IRS' }).click();
  await expect(page.getByTestId('workspace-summary')).toContainText('1 line(s)');
  await settled(page, 'Lines');

  // The end, then each leg's terms, typed cell after cell; Tab commits and moves right.
  await page.keyboard.press('ControlOrMeta+Shift+G');
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  for (let i = 0; i < END; i++) await page.keyboard.press('ArrowRight');
  const typed: (string | null)[] = ['5y', null, 'JPY', 'TONA', '1,000,000,000', null, null, '12m', null, null,
    'JPY', 'TONA', '1,000,000,000', 'spread', '0', '12m'];
  for (const text of typed) {
    if (text !== null) await page.keyboard.type(text);
    await page.keyboard.press('Tab');
  }
  await expect(page.getByTestId('workspace-summary')).toContainText('revision');
  await expect(cell(page, LEG2_QUOTE)).toHaveText('0');
  await page.keyboard.press('ControlOrMeta+Home');
  await expect(cell(page, END)).toHaveText('5y');
  // A billion fits its column whole: a notional shown as #### would be unreadable, not wrong, but still unusable.
  await expect(cell(page, LEG1_NOTIONAL)).toHaveText('1,000,000,000');

  await page.getByTestId('live-toggle').click();
  await expect(cell(page, STATUS)).toHaveText('priced');
  await expect(cell(page, PV)).toHaveText(/^-?[\d,]+$/);
  await expect(page.getByTestId('live-frame')).toContainText('simulated');

  await page.getByRole('button', { name: 'Capture' }).click();
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  await expect(page.getByRole('tab', { name: /^Capture / })).toHaveAttribute('aria-selected', 'true');

  // Leaving the page only detached it: back on it, the page attaches to the same live subscription by itself.
  await page.getByRole('tab', { name: 'Rates 2026-10-27' }).click();
  await settled(page, 'Lines');
  await expect(page.getByTestId('live-toggle')).toHaveText('Stop live');
  await expect(page.getByTestId('live-frame')).toContainText('live');
  await command(page, 'Price Rates workspace');
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  // The Run's view is a grid titled Lines too; only the Run's tab panel is mounted now.
  await expect(page.getByRole('tabpanel').getByLabel('Lines', { exact: true }).getByRole('row', { name: /^L\d+ priced / })).toBeVisible();
});

test('C43, C39: a leg\'s second level opens and closes on request, and a line without terms shows no schedule', async ({ page }) => {
  await open(page);
  await newWorkspace(page);
  await page.getByRole('button', { name: 'Add IRS' }).click();
  await settled(page, 'Lines');
  // The group's caption says whether its second level is shown; its columns may lie beyond the Viewport.
  await expect(lines(page).getByRole('columnheader', { name: 'Leg 1 ▸' })).toHaveCount(1);

  await page.getByRole('button', { name: 'Leg 1 details' }).click();
  await settled(page, 'Lines');
  await expect(lines(page).getByRole('columnheader', { name: 'Leg 1 ▾' })).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Leg 1 details' })).toHaveAttribute('aria-pressed', 'true');
  await page.getByRole('button', { name: 'Leg 1 details' }).click();
  await settled(page, 'Lines');
  await expect(lines(page).getByRole('columnheader', { name: 'Leg 1 ▸' })).toHaveCount(1);

  await firstLine(page).getByRole('button', { name: 'Cashflows' }).click();
  await expect(page.getByTestId('cashflows')).toContainText('generates no schedule');
});
