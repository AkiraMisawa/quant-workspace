import type { Page, Request } from '@playwright/test';
import { EventSchemas } from '@ag-ui/core/schemas';
import { command, expect, open, settled, test } from './fixtures';

// ADR-0007: the AI pane over AG-UI, answered by the scripted agent (no model), which acts only through the registered
// commands. The host runs with the development settings: the unconfirmed convention proposal, figures simulated.

const SWAP = 'make a 10y JPY TONA swap, pay fixed, 1bn, fixed 12m float 12m, spread 0, valuation date 2026-10-27';

const pane = (page: Page) => page.getByTestId('ai-pane');
const cards = (page: Page) => pane(page).getByTestId('ai-card');
const chat = (page: Page) => page.getByRole('textbox', { name: 'Ask the AI' });

/**
 * Every event of the run a request posted, each checked against AG-UI's own schema (@ag-ui/core 1.0.3). The browser
 * keeps no copy of a stream's body, so the run is read again from the server, as a reader that lost it would (AI04).
 */
async function agUiEvents(page: Page, posted: Request): Promise<{ readonly type: string }[]> {
  const { threadId, runId } = posted.postDataJSON() as { threadId: string; runId: string };
  const body = await (await page.request.get(`/api/agent/threads/${threadId}/runs/${runId}/events`)).text();
  const events = body.split('\n').filter((l) => l.startsWith('data: ')).map((l) => JSON.parse(l.slice(6)) as unknown);
  return events.map((e) => EventSchemas.parse(e));
}

test('ADR-0007 §1, §7, ADR-0006 §7: `ai` on the command line makes a line and ticks it, behind the panel in use', async ({ page }) => {
  await open(page);
  await command(page, 'open pricing db');
  await expect(page.getByRole('tab', { name: 'Pricing DB' })).toHaveAttribute('aria-selected', 'true');

  const posted = page.waitForRequest((r) => r.url().endsWith('/api/agent/runs') && r.method() === 'POST');
  await command(page, 'ai', `${SWAP} and start ticking`);

  await expect(cards(page)).toHaveCount(1);
  await expect(cards(page).first()).toHaveAttribute('data-status', 'done');
  await expect(pane(page).getByTestId('ai-runtime')).toHaveText('scripted · no model');
  const steps = cards(page).first().getByTestId('ai-step');
  await expect(steps).toHaveCount(2);
  await expect(steps.nth(0)).toHaveAttribute('data-outcome', 'done');
  await expect(steps.nth(1)).toHaveAttribute('data-outcome', 'done');
  await expect(cards(page).first().getByTestId('ai-reply')).toContainText('is ticking live');

  const types = (await agUiEvents(page, await posted)).map((e) => e.type);
  expect(types[0]).toBe('RUN_STARTED');
  expect(types.at(-1)).toBe('RUN_FINISHED');
  expect(types.filter((t) => t === 'TOOL_CALL_RESULT')).toHaveLength(2);

  // What the AI opened waits behind the panel in use, and the keyboard was not moved into the pane.
  await expect(page.getByRole('tab', { name: 'Pricing DB' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('tab', { name: 'Rates 2026-10-27' })).toHaveAttribute('aria-selected', 'false');
  await expect(chat(page)).not.toBeFocused();

  await cards(page).first().getByRole('button', { name: 'Show Rates 2026-10-27' }).click();
  await expect(page.getByRole('tab', { name: 'Rates 2026-10-27' })).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'Lines');
  await expect(page.getByLabel('Lines', { exact: true }).getByRole('row', { name: /^L\d+ / })).toHaveCount(1);
  await expect(page.getByTestId('live-toggle')).toHaveText('Stop live');
});

test('ADR-0005 §4, ADR-0006 §2: in the chat `/` opens its own commands, and a missing term is asked for, not filled in', async ({ page }) => {
  await open(page);
  await page.getByRole('button', { name: 'Show the AI pane' }).click();
  await chat(page).click();

  await page.keyboard.type('/');
  await expect(page.getByRole('listbox', { name: 'Chat commands' })).toBeVisible();
  await expect(page.getByRole('combobox', { name: 'Command' })).toHaveCount(0);
  await page.keyboard.press('Escape');
  await expect(chat(page)).toHaveValue('');

  await page.keyboard.type('make a 10y JPY TONA swap, 1bn, valuation date 2026-10-27');
  await page.keyboard.press('Enter');
  await expect(cards(page).first()).toHaveAttribute('data-status', 'done');
  await expect(cards(page).first().getByTestId('ai-step')).toHaveAttribute('data-outcome', 'refused');
  await expect(cards(page).first().getByTestId('ai-reply')).toContainText('direction');
  await expect(page.getByRole('tab', { name: /^Rates / })).toHaveCount(0);

  await page.keyboard.type('pay fixed, fixed 12m float 12m, spread 0');
  await page.keyboard.press('Enter');
  await expect(cards(page)).toHaveCount(2);
  await expect(cards(page).nth(1)).toHaveAttribute('data-status', 'done');
  await expect(cards(page).nth(1).getByTestId('ai-step')).toHaveAttribute('data-outcome', 'done');
  await expect(page.getByRole('tab', { name: 'Rates 2026-10-27' })).toHaveCount(1);

  await page.keyboard.type('/new');
  await page.keyboard.press('Enter');
  await expect(cards(page)).toHaveCount(0);
  await expect(chat(page)).toBeFocused();
});

test('ADR-0007 §7, UX03: on a phone-width screen the pane covers the panels, its input on screen, and takes no keyboard', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await open(page);
  await page.getByRole('button', { name: 'Show the AI pane' }).click();

  await expect(chat(page)).toBeInViewport();
  await expect(chat(page)).not.toBeFocused();
  await chat(page).click();
  await page.keyboard.type(`${SWAP} and start ticking`);
  await page.keyboard.press('Enter');
  await expect(cards(page).first()).toHaveAttribute('data-status', 'done');
  await expect(cards(page).first().getByTestId('ai-reply')).toContainText('is ticking live');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);

  await cards(page).first().getByRole('button', { name: 'Show Rates 2026-10-27' }).click();
  await page.getByRole('button', { name: 'Close the AI pane' }).click();
  await expect(page.getByRole('tab', { name: 'Rates 2026-10-27' })).toHaveAttribute('aria-selected', 'true');
});
