import type { Page } from '@playwright/test';
import { command, expect, open, settled, test } from './fixtures';

// Slice 02 (docs/plans/slice-02-xva-workspace.md): the XVA workspace and the Pricing DB, driven by keyboard.

async function newWorkspace(page: Page, registration: string, trades: number): Promise<void> {
  await command(page, 'new xva');
  const dialog = page.getByRole('dialog', { name: 'New XVA workspace' });
  await expect(dialog.getByRole('combobox', { name: 'Counterparty' })).toBeFocused();
  await page.keyboard.type(registration);
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('combobox', { name: 'Counterparty' })).toHaveValue(new RegExp(`^${registration} `));
  await dialog.getByLabel('Existing trades (synthetic)').fill(String(trades));
  await page.keyboard.press('Enter');
  await expect(page.getByRole('tab', { name: `XVA ${registration}` })).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'Trades');
}

const trade = (page: Page, id: string) => page.getByLabel('Trades').getByRole('row', { name: new RegExp(`^${id} `) });

/** Puts the grid's Focus on a cell of the first row, `right` columns from the first. */
async function focusCell(page: Page, right: number): Promise<void> {
  await page.keyboard.press('ControlOrMeta+Shift+G');
  // The grid's first key only places its Focus (ex-grid ADR-0037); the next ones move it.
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  for (let i = 0; i < right; i++) await page.keyboard.press('ArrowRight');
}

const CSA = 8;

test('docs/03, ADR-0003: a new trade takes the main CSA, a CSA typed in moves its curve, and Price lands in the Pricing DB', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 0);
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(CSA)).toHaveText('CSA-A');
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(CSA + 1)).toHaveText('jpy_ois');

  await focusCell(page, CSA);
  await page.keyboard.type('CSA-B');
  await page.keyboard.press('Enter');
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(CSA)).toHaveText('CSA-B');
  // The curve follows the CSA on the server; nothing here computed it.
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(CSA + 1)).toHaveText('usd_sofr');
  await expect(page.getByTestId('workspace-summary')).toContainText('1 edit(s)');

  await command(page, 'price');
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  await expect(page.getByLabel('CVA and FVA (simulated)').getByRole('row', { name: /^CVA / })).toBeVisible();
  const runId = await page.locator('[data-testid=run-status] + p').textContent();
  expect(runId).toMatch(/^run-/);

  await command(page, 'open pricing db');
  await settled(page, 'Pricing DB');
  await expect(page.getByLabel('Pricing DB').getByRole('row', { name: new RegExp(`${runId}`) })).toContainText('pricer');
  await page.getByRole('radiogroup', { name: 'Domain' }).getByText('XVA', { exact: true }).click();
  await expect(page.getByRole('radio', { name: 'XVA' })).toBeChecked();
  await settled(page, 'Pricing DB');
  const row = page.getByLabel('Pricing DB').getByRole('row', { name: new RegExp(`${runId}`) });
  // The domain's figures follow the Run's own columns: value date, registration, trades Before and After.
  await expect(row.getByRole('gridcell').nth(5)).toHaveText('2026-10-26');
  await expect(row.getByRole('gridcell').nth(6)).toHaveText('ABC/LE-01');
  await expect(row.getByRole('gridcell').nth(7)).toHaveText('0');
  await expect(row.getByRole('gridcell').nth(8)).toHaveText('3');
});

test('ADR-0003 §6: a counterparty change asks before dropping its edits, and keeps a new trade\'s own', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 0);
  await focusCell(page, 3);
  await page.keyboard.type('7000000');
  await page.keyboard.press('Enter');
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(3)).toHaveText('7,000,000');
  await focusCell(page, CSA);
  await page.keyboard.type('CSA-B');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('workspace-summary')).toContainText('2 edit(s)');

  await page.getByRole('combobox', { name: 'Counterparty' }).click();
  await page.getByRole('option', { name: /^ABC\/LE-02 / }).click();
  const confirm = page.getByRole('dialog', { name: 'Change counterparty' });
  await expect(confirm).toContainText('drops 1 edit(s)');
  await expect(confirm).toContainText('xva.assignment');
  await confirm.getByRole('button', { name: 'Drop 1 and change' }).click();

  await expect(page.getByTestId('notice')).toContainText('1 edit(s) carried');
  await settled(page, 'Trades');
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(3)).toHaveText('7,000,000');
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(CSA)).toHaveText('CSA-C');
});
