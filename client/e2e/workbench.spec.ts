import { command, expect, open, settled, test } from './fixtures';

// Slice 01 (docs/plans/slice-01-generic-workbench.md): the generic workbench driven as a user drives it.

test('UX01, CO05: a workflow runs from the palette by keyboard alone, and its row actions open a view and run a workflow', async ({ page }) => {
  await open(page);
  await command(page, 'Run Station summary');
  // The run dialog's first field holds the keyboard once the dialog is up.
  await expect(page.getByRole('dialog', { name: 'Station summary' }).getByLabel('Days')).toBeFocused();
  await page.keyboard.type('5');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  await expect(page.getByRole('heading', { name: 'Mean by station' })).toBeVisible();

  // A second registered view over the same retained outputs, chosen from the palette.
  await command(page, 'Show as Station summary');
  await settled(page, 'Summary by station');

  // Row action that opens a view over retained data: Show readings (the first action in its cell).
  await page.keyboard.press('ControlOrMeta+Shift+G');
  // The grid's first key only places its Focus (ex-grid ADR-0037); the next one moves it.
  await page.keyboard.press('Home');
  await page.keyboard.press('ControlOrMeta+ArrowRight');
  await page.keyboard.press('Space');
  await page.keyboard.press('Space');
  await expect(page.getByRole('tab', { name: 'Readings' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByLabel('Daily readings')).toHaveAttribute('data-rows', '5');

  // Row action that runs a workflow with a bound parameter: Summarise this station.
  await page.keyboard.press('ControlOrMeta+Alt+ArrowLeft');
  await settled(page, 'Summary by station');
  await page.keyboard.press('ControlOrMeta+Shift+G');
  // The grid's first key only places its Focus (ex-grid ADR-0037); the next one moves it.
  await page.keyboard.press('Home');
  await page.keyboard.press('ControlOrMeta+ArrowRight');
  await page.keyboard.press('Space');
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('Space');
  await expect(page.getByRole('tab', { name: 'Single station summary' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  await expect(page.getByText('stationId ST-01')).toBeVisible();
});

test('UX01: a context rebound by keyboard reaches the next run, whose failed item stays unavailable', async ({ page }) => {
  await open(page);
  await command(page, 'Set Region to South');
  await expect(page.getByRole('combobox', { name: 'Region' })).toHaveValue('South');
  await command(page, 'Run Station summary');
  await expect(page.getByRole('dialog', { name: 'Station summary' }).getByLabel('Days')).toBeFocused();
  await page.keyboard.type('3');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('run-status')).toHaveText('partiallySucceeded');
  await expect(page.getByText(/^region South · asOf/)).toBeVisible();
  await command(page, 'Show as Station summary');
  // The failed station is a row of its own, unavailable, with its figures blank and never zero.
  const row = page.getByLabel('Summary by station').getByRole('row', { name: /^ST-05 / });
  await expect(row.getByRole('gridcell')).toHaveText(['ST-05', 'unavailable', '', '', '', '', 'Synthetic station ST-05 is offline.', /Show readings/]);
});

test('the palette gives the keyboard back to the grid it was opened from', async ({ page }) => {
  await open(page);
  await command(page, 'Run Station summary');
  await expect(page.getByRole('dialog', { name: 'Station summary' }).getByLabel('Days')).toBeFocused();
  await page.keyboard.type('2');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  await settled(page, 'Mean by station');
  await page.keyboard.press('ControlOrMeta+Shift+G');
  const grid = page.getByLabel('Mean by station', { exact: true }).getByRole('grid');
  await expect(grid).toBeFocused();
  await page.keyboard.press('ControlOrMeta+K');
  await expect(page.getByRole('textbox', { name: 'Command' })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(grid).toBeFocused();
});

test('UX03: the theme toggles from the keyboard', async ({ page }) => {
  await open(page);
  const html = page.locator('html');
  await expect(html).toHaveAttribute('data-mantine-color-scheme', 'dark');
  await page.keyboard.press('ControlOrMeta+Alt+T');
  await expect(html).toHaveAttribute('data-mantine-color-scheme', 'light');
});

test('UX03: on a phone-width screen the page does not scroll sideways and runs by pointer', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await open(page);
  await page.getByRole('button', { name: 'Open command palette' }).click();
  await page.getByRole('textbox', { name: 'Command' }).fill('Run Station summary');
  await page.locator('.mantine-Spotlight-action', { hasText: 'Run Station summary' }).click();
  const days = page.getByRole('dialog', { name: 'Station summary' }).getByLabel('Days');
  await days.fill('4');
  await page.getByRole('button', { name: 'Run', exact: true }).click();
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  await expect(page.getByRole('heading', { name: 'Mean by station' })).toBeVisible();
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBe(0);
});
