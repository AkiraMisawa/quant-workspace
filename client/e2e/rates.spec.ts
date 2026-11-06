import type { Page } from '@playwright/test';
import { command, expect, open, settled, test } from './fixtures';

// Slice 03 (docs/plans/slice-03-rates-line-pricer.md): a line typed by keyboard, priced live, captured and
// priced once. The host runs with the development settings: the unconfirmed convention proposal and a synthetic
// market, every figure simulated.

async function newWorkspace(page: Page): Promise<void> {
  await command(page, 'new rates');
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

// The inputs first and the result after them (owner, 2026-11-04): line, actions, trade, two legs, terms, result.
const END = 4;
const LEG1_NOTIONAL = 8;
const LEG2_QUOTE = 18;
const STATUS = 25;
const PV = 26;
const ISSUES = 29;

/** Moves the Focus to a column of the first line, from the keyboard, so the grid scrolls it into view. */
async function goTo(page: Page, column: number): Promise<void> {
  await page.keyboard.press('ControlOrMeta+Shift+G');
  await page.keyboard.press('ControlOrMeta+Home');
  for (let i = 0; i < column; i++) await page.keyboard.press('ArrowRight');
}

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
  const typed: (string | null)[] = ['5y', null, 'JPY', 'TONA', '1000', null, null, '12m', null, null,
    'JPY', 'TONA', '1000', 'spread', '0', '12m'];
  for (const text of typed) {
    if (text !== null) await page.keyboard.type(text);
    await page.keyboard.press('Tab');
  }
  await expect(page.getByTestId('workspace-summary')).toContainText('revision');
  await expect(cell(page, LEG2_QUOTE)).toHaveText('0');
  await page.keyboard.press('ControlOrMeta+Home');
  await expect(cell(page, END)).toHaveText('5y');
  // A notional is typed and shown in millions (owner, 2026-11-04): 1000 is a billion, and the server priced it so.
  await expect(cell(page, LEG1_NOTIONAL)).toHaveText('1,000');

  await page.getByTestId('live-toggle').click();
  // The line in hand shows its result above the grid, though the result columns are off screen (owner, 2026-11-04).
  await expect(page.getByTestId('line-result-status')).toHaveText('priced');
  await expect(page.getByTestId('line-result-pv')).toHaveText(/^PV -?[\d,]+ JPY$/);
  // The result columns stand after the inputs, so they are scrolled to.
  await goTo(page, PV);
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
  await command(page, 'price');
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  // The Run's view is a grid titled Lines too; only the Run's tab panel is mounted now.
  await expect(page.getByRole('tabpanel').getByLabel('Lines', { exact: true }).getByRole('row', { name: /^L\d+ priced / })).toBeVisible();
});

test('C43, C39: a leg\'s second level opens and closes on request, and a line without terms shows no schedule', async ({ page }) => {
  await open(page);
  await newWorkspace(page);
  await page.getByRole('button', { name: 'Add IRS' }).click();
  await settled(page, 'Lines');
  // Each leg's Header Group draws ExGrid's toggle (ADR-0400); the page answers it with the leg's second level.
  const toggle = lines(page).getByRole('button', { name: 'Expand Leg 1' });
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await expect(lines(page).getByRole('columnheader', { name: 'Day count' })).toHaveCount(0);

  await toggle.click();
  await settled(page, 'Lines');
  await expect(lines(page).getByRole('button', { name: 'Collapse Leg 1' })).toHaveAttribute('aria-expanded', 'true');
  await lines(page).getByRole('button', { name: 'Collapse Leg 1' }).click();
  await settled(page, 'Lines');
  await expect(lines(page).getByRole('button', { name: 'Expand Leg 1' })).toHaveAttribute('aria-expanded', 'false');

  await firstLine(page).getByRole('button', { name: 'Flows' }).click();
  await expect(page.getByTestId('cashflows')).toContainText('generates no schedule');
});

test('ADR-0008: a line is made from short words in any order, read against the product the server describes', async ({ page, problems }) => {
  await open(page);
  await newWorkspace(page);
  const line = page.getByRole('combobox', { name: 'Command' });
  const options = page.getByRole('listbox');

  // `/` opens the line from outside a text field or grid; the command is found by the start of its words.
  await page.getByRole('tab', { name: 'Rates 2026-10-27' }).focus();
  await page.keyboard.press('/');
  await expect(line).toBeFocused();
  await page.keyboard.type('ne ir');
  await expect(options.getByRole('option').first()).toContainText('new irs');
  await page.keyboard.press('Tab');
  await expect(line).toHaveValue('new irs ');

  // Each word is completed from the description: a currency the server's profiles name, taken with Tab.
  await page.keyboard.type('jp');
  await expect(options.getByRole('option').first()).toContainText('jpy');
  await page.keyboard.press('Tab');
  await expect(line).toHaveValue('new irs jpy ');

  // The rest in any order; the line shows how each word was read. The index and the frequencies come from the
  // JPY profile's template and the quotes from C47 (at the money), so nothing more is typed.
  await page.keyboard.type('3bn 10y rec');
  const pending = page.getByTestId('command-pending');
  await expect(pending).toContainText('notional 3bn');
  await expect(pending).toContainText('end 10y');
  await expect(pending).toContainText('direction rec');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(page.getByTestId('workspace-summary')).toContainText('1 line(s)');
  await expect(cell(page, END)).toHaveText('10y');
  await expect(cell(page, LEG1_NOTIONAL)).toHaveText('3,000');
  await settled(page, 'Lines');
  // A line that can be priced has no issues.
  await goTo(page, ISSUES);
  await expect(cell(page, ISSUES)).toHaveText('');

  // What no default supplies is refused by name, and no line is made.
  await page.getByRole('tab', { name: 'Rates 2026-10-27' }).focus();
  await page.keyboard.press('/');
  await expect(line).toBeFocused();
  await page.keyboard.type('new irs 5y');
  await page.keyboard.press('Enter');
  await expect(page.getByText(/The line was not created: currency is missing/)).toBeVisible();
  await expect(page.getByTestId('workspace-summary')).toContainText('1 line(s)');
  // The refusal is the server's 422, which the browser reports; it is the one console message this test expects.
  expect(problems.splice(0)).toEqual(['error: Failed to load resource: the server responded with a status of 422 (Unprocessable Entity)']);
});
