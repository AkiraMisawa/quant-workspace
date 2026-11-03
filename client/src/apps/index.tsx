import { useState, type ReactNode } from 'react';
import { Button, Group, Modal, NumberInput, Select, Stack, TextInput } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import type { Command, CommandArgument } from '../platform/commands/commands';
import { parserOf } from './rates/lines';
import { parseNumber } from './xva/editing';
import { useWorkspace, useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import type { Panel, WorkspaceState } from '../platform/workspace/workspaceStore';
import { PricingDbPanel } from './history/PricingDbPanel';
import { httpRatesApi, type RatesApi } from './rates/api';
import { ratesCommandsOf } from './rates/commands';
import { RatesWorkspacePanel } from './rates/RatesWorkspacePanel';
import { httpXvaApi, type XvaApi } from './xva/api';
import { priceOf } from './xva/commands';
import { XvaWorkspacePanel } from './xva/XvaWorkspacePanel';
import { registerAiContext, registerAiEffect } from '../ai/effects';

// The fixed applications (docs/01): pages a domain offers beside the composed workflows. Each is reached
// through commands, so the command line and a button cannot differ (ADR-0006).

export const XVA_WORKSPACE = 'xva.workspace';
export const PRICING_DB = 'history.pricingDb';
export const RATES_WORKSPACE = 'rates.workspace';
const NEW_XVA = 'xva.new';
const NEW_RATES = 'rates.new';

let xvaApi: XvaApi = httpXvaApi;
/** For tests: the XVA endpoints a page talks to. */
export function setXvaApi(api: XvaApi): void {
  xvaApi = api;
}

let ratesApi: RatesApi = httpRatesApi;
/** For tests: the Rates endpoints a page talks to. */
export function setRatesApi(api: RatesApi): void {
  ratesApi = api;
}

// What the AI pane knows of Rates (ADR-0007 §7, §8): an open workspace is the context a request acts on, a line the AI
// creates opens its workspace behind the active panel, and what the AI changes is fetched again where it is shown.
registerAiContext(RATES_WORKSPACE, (panel) => panel.params.workspaceId === undefined ? undefined : {
  description: 'The Rates workspace open in the active panel',
  value: JSON.stringify({ kind: 'rates.workspace', workspaceId: panel.params.workspaceId }),
});
registerAiEffect('rates.line.create', (outputs, { workspace, queryClient }) => {
  const line = outputs.line as { readonly workspaceId: string; readonly valuationDate: string };
  void queryClient.invalidateQueries({ queryKey: ['rates', line.workspaceId] });
  const title = `Rates ${line.valuationDate}`;
  return { panelId: workspace.openPanelBehind(RATES_WORKSPACE, title, { workspaceId: line.workspaceId }), title };
});
for (const capability of ['rates.live.start', 'rates.live.stop'])
  registerAiEffect(capability, (outputs, { queryClient }) => {
    const subscription = outputs.subscription as { readonly workspaceId: string };
    void queryClient.invalidateQueries({ queryKey: ['rates', subscription.workspaceId] });
    return undefined;
  });

export function AppPanel({ panel }: { readonly panel: Extract<Panel, { kind: 'app' }> }): ReactNode {
  switch (panel.app) {
    case XVA_WORKSPACE: {
      const id = panel.params.workspaceId;
      if (id === undefined) throw new Error('An XVA workspace panel names its workspace.');
      return <XvaWorkspacePanel panelId={panel.id} workspaceId={id} api={xvaApi} />;
    }
    case RATES_WORKSPACE: {
      const id = panel.params.workspaceId;
      if (id === undefined) throw new Error('A Rates workspace panel names its workspace.');
      return <RatesWorkspacePanel panelId={panel.id} workspaceId={id} api={ratesApi} />;
    }
    case PRICING_DB:
      return <PricingDbPanel panelId={panel.id} />;
    default:
      throw new Error(`No app draws ${panel.app}.`);
  }
}

export function appCommands(state: Pick<WorkspaceState, 'showApp' | 'openAppDialog' | 'panels' | 'activePanelId'>): Command[] {
  const active = state.panels.find((p) => p.id === state.activePanelId);
  // Looked up when the command runs: the page registers its commands once it has mounted, after this list is made.
  const xva = active?.kind === 'app' && active.app === XVA_WORKSPACE ? active.id : undefined;
  const rates = active?.kind === 'app' && active.app === RATES_WORKSPACE ? active.id : undefined;
  const page = () => {
    const commands = ratesCommandsOf(rates);
    if (commands === undefined) throw new Error(`The Rates workspace in panel ${rates ?? '(none)'} registered no commands.`);
    return commands;
  };
  const onRates = rates === undefined ? { unavailable: 'Open a Rates workspace first.' } : { target: active!.title };
  const priceTarget = xva !== undefined || rates !== undefined ? { target: active!.title } : { unavailable: 'Open an XVA or Rates workspace to price it.' };
  return [
    {
      id: 'xva.new',
      words: 'new xva',
      title: 'New XVA workspace for a counterparty',
      group: 'XVA',
      run: () => state.openAppDialog(NEW_XVA),
    },
    {
      id: 'workspace.price',
      words: 'price',
      title: 'Price the workspace as it stands',
      group: 'Pricing',
      ...priceTarget,
      run: () => {
        if (rates !== undefined) { page().price(); return; }
        const price = priceOf(xva);
        if (price === undefined) throw new Error(`The XVA workspace in panel ${xva ?? '(none)'} registered no Price.`);
        price();
      },
    },
    {
      id: 'rates.new',
      words: 'new rates',
      title: 'New Rates workspace (IRS and CCS lines)',
      group: 'Rates',
      run: () => state.openAppDialog(NEW_RATES),
    },
    {
      id: 'rates.line.irs',
      words: 'new irs',
      title: 'Add an interest rate swap from its terms',
      group: 'Rates',
      ...onRates,
      args: irsArguments(() => ratesCommandsOf(rates)?.conventions() ?? []),
      run: (a) => page().createLine(irsTerms(a)),
    },
    {
      id: 'rates.line.add',
      words: 'add line',
      title: 'Add an empty line to fill in the grid',
      group: 'Rates',
      ...onRates,
      args: [{ name: 'product', required: true, complete: () => [{ value: 'irs', detail: 'interest rate swap' }, { value: 'ccs', detail: 'cross-currency swap' }],
        check: (v) => (v === 'irs' || v === 'ccs' ? undefined : 'irs or ccs') }],
      run: (a) => page().addLine(a.product as 'irs' | 'ccs'),
    },
    { id: 'rates.live.start', words: 'live start', title: 'Start ticking the workspace', group: 'Rates', ...onRates, run: () => page().startLive() },
    { id: 'rates.live.stop', words: 'live stop', title: 'Stop ticking the workspace, for everyone watching', group: 'Rates', ...onRates, run: () => page().stopLive() },
    { id: 'rates.capture', words: 'capture', title: 'Save the live frame on screen as a Run', group: 'Rates', ...onRates, run: () => page().capture() },
    {
      id: 'history.open',
      words: 'open pricing db',
      title: 'Open the Pricing DB of past Runs',
      group: 'History',
      run: () => state.showApp(PRICING_DB, 'Pricing DB', {}),
    },
  ];
}

const TENORS = ['1y', '2y', '3y', '5y', '7y', '10y', '15y', '20y', '30y'];
const FREQUENCIES = [{ value: '1m' }, { value: '3m' }, { value: '6m' }, { value: '12m' }];

/** A notional as typed on the line: a number, or one with k, m or bn after it (`3bn`, `500m`). */
export function parseNotional(text: string): number | undefined {
  const m = /^(.+?)(k|m|bn)?$/i.exec(text.trim());
  if (m === null) return undefined;
  const n = parseNumber(m[1]!);
  if (n === undefined || n <= 0) return undefined;
  const scale = { k: 1e3, m: 1e6, bn: 1e9 }[(m[2] ?? '').toLowerCase() as 'k' | 'm' | 'bn'] ?? 1;
  return n * scale;
}

const parsed = (field: string) => (value: string): string | undefined => {
  const result = parserOf(field)!(value);
  return result.ok ? undefined : result.message;
};

/**
 * `new irs`: the basic terms of an IRS (C40), one leg fixed and one floating on the same currency, index and
 * notional. A quote left out is the leg solved at par; one of rate= or spread= is needed (a line with both legs at
 * par is refused, by the server, with its reason).
 */
function irsArguments(conventions: () => readonly { readonly currency: string; readonly index: string }[]): CommandArgument[] {
  return [
    { name: 'currency', required: true, complete: () => [...new Set(conventions().map((c) => c.currency))].map((value) => ({ value })) },
    { name: 'index', required: true, complete: (g) => conventions().filter((c) => g.currency === undefined || c.currency === g.currency.toUpperCase()).map((c) => ({ value: c.index, detail: c.currency })) },
    { name: 'end', required: true, hint: 'a tenor such as 10y, or a date', complete: () => TENORS.map((value) => ({ value })), check: parsed('end') },
    { name: 'notional', required: true, hint: 'such as 1000000000, 3bn or 500m', check: (v) => (parseNotional(v) === undefined ? 'a positive number, with k, m or bn if you like' : undefined) },
    { name: 'direction', required: true, complete: () => [{ value: 'pay', detail: 'pay fixed' }, { value: 'receive', detail: 'receive fixed' }], check: parsed('direction') },
    { name: 'fixed-frequency', required: true, complete: () => FREQUENCIES, check: parsed('leg1.paymentFrequency') },
    { name: 'float-frequency', required: true, complete: () => FREQUENCIES, check: parsed('leg2.paymentFrequency') },
    { name: 'rate', required: false, hint: 'the fixed rate in %; left out, it is solved at par', check: parsed('leg1.quote') },
    { name: 'spread', required: false, hint: 'the floating spread in %; left out, it is solved at par', check: parsed('leg2.quote') },
  ];
}

function irsTerms(a: Readonly<Record<string, string>>): Record<string, unknown> {
  const value = (field: string, text: string) => {
    const result = parserOf(field)!(text);
    if (!result.ok) throw new Error(`${field} was checked on the line and is refused now: ${result.message}`);
    return result.value;
  };
  const notional = parseNotional(a.notional!);
  if (notional === undefined) throw new Error('The notional was checked on the line and is refused now.');
  const terms: Record<string, unknown> = { product: 'irs', end: value('end', a.end!), direction: value('direction', a.direction!) };
  for (const [leg, frequency] of [[1, a['fixed-frequency']!], [2, a['float-frequency']!]] as const) {
    terms[`leg${leg}.currency`] = a.currency!.toUpperCase();
    terms[`leg${leg}.index`] = a.index!.toUpperCase();
    terms[`leg${leg}.notional`] = notional;
    terms[`leg${leg}.paymentFrequency`] = value(`leg${leg}.paymentFrequency`, frequency);
  }
  if (a.rate !== undefined) { terms['leg1.quoteKind'] = 'FixedRatePercent'; terms['leg1.quote'] = value('leg1.quote', a.rate); }
  if (a.spread !== undefined) { terms['leg2.quoteKind'] = 'FloatSpreadPercent'; terms['leg2.quote'] = value('leg2.quote', a.spread); }
  return terms;
}

/** The dialogs apps open. Enter submits; Escape closes. */
export function AppDialogs() {
  const dialog = useWorkspace((s) => s.appDialog);
  const close = useWorkspace((s) => s.openAppDialog);
  return (
    <>
      <Modal opened={dialog === NEW_XVA} onClose={() => close(undefined)} title="New XVA workspace" trapFocus returnFocus transitionProps={{ duration: 0 }}>
        {dialog === NEW_XVA && <NewXvaWorkspace onDone={() => close(undefined)} />}
      </Modal>
      <Modal opened={dialog === NEW_RATES} onClose={() => close(undefined)} title="New Rates workspace" trapFocus returnFocus transitionProps={{ duration: 0 }}>
        {dialog === NEW_RATES && <NewRatesWorkspace onDone={() => close(undefined)} />}
      </Modal>
    </>
  );
}

function NewXvaWorkspace({ onDone }: { readonly onDone: () => void }) {
  const workspace = useWorkspaceContext();
  const registrations = useQuery({ queryKey: ['xva', 'registrations'], queryFn: () => xvaApi.registrations(), staleTime: Infinity });
  const [registration, setRegistration] = useState<string | null>(null);
  const [valueDate, setValueDate] = useState('2026-10-26');
  const [size, setSize] = useState<number | string>(1000);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);

  const submit = async () => {
    if (registration === null) { setError('Choose a counterparty.'); return; }
    if (typeof size !== 'number' || !Number.isInteger(size) || size < 0) { setError('The portfolio size is a whole number.'); return; }
    setBusy(true);
    try {
      const created = await xvaApi.create(registration, valueDate, size);
      workspace.store.getState().showApp(XVA_WORKSPACE, `XVA ${created.registration.key}`, { workspaceId: created.workspaceId });
      onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={(e) => { e.preventDefault(); void submit(); }}>
      <Stack gap="sm">
        <Select label="Counterparty" data-autofocus searchable value={registration} onChange={setRegistration} error={error}
          data={(registrations.data ?? []).map((r) => ({ value: r.key, label: `${r.key} · ${r.name}` }))} />
        <TextInput label="Value date" type="date" value={valueDate} onChange={(e) => setValueDate(e.currentTarget.value)} />
        <NumberInput label="Existing trades (synthetic)" min={0} max={100000} allowDecimal={false} value={size} onChange={setSize} />
        <Group justify="flex-end"><Button type="submit" loading={busy}>Create</Button></Group>
      </Stack>
    </form>
  );
}

function NewRatesWorkspace({ onDone }: { readonly onDone: () => void }) {
  const workspace = useWorkspaceContext();
  const [valuationDate, setValuationDate] = useState('2026-10-27');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);

  const submit = async () => {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(valuationDate)) { setError('Choose a valuation date.'); return; }
    setBusy(true);
    try {
      const created = await ratesApi.create(valuationDate);
      workspace.store.getState().showApp(RATES_WORKSPACE, `Rates ${created.valuationDate}`, { workspaceId: created.workspaceId });
      onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={(e) => { e.preventDefault(); void submit(); }}>
      <Stack gap="sm">
        <TextInput label="Valuation date" type="date" data-autofocus value={valuationDate} error={error} onChange={(e) => setValuationDate(e.currentTarget.value)} />
        <Group justify="flex-end"><Button type="submit" loading={busy}>Create</Button></Group>
      </Stack>
    </form>
  );
}
