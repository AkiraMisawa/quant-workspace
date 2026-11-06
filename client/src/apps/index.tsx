import { useState, type ReactNode } from 'react';
import { Button, Group, Modal, NumberInput, Select, Stack, TextInput } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import type { Command } from '../platform/commands/commands';
import { termCompletions, termParts, type ProductDescriptor } from '../platform/products';
import { parserOf } from './rates/lines';
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
    ...productCommands(rates === undefined ? [] : ratesCommandsOf(rates)?.products() ?? [], onRates, (product, terms) => page().newLine(product, terms)),
    {
      id: 'rates.defaults',
      words: 'set defaults',
      title: 'Set the workspace defaults for new lines (currency, notional, direction)',
      group: 'Rates',
      ...onRates,
      args: [
        { name: 'currency', required: false, hint: 'a currency such as JPY; none clears it' },
        { name: 'notional', required: false, hint: 'in millions (1000 is a billion), or 3bn, 500mm; none clears it', check: (v) => (v === 'none' ? undefined : parsed('leg1.notional')(v)) },
        { name: 'direction', required: false, complete: () => [{ value: 'pay' }, { value: 'receive' }, { value: 'none' }], check: (v) => (v === 'none' ? undefined : parsed('direction')(v)) },
      ],
      run: (a) => page().setDefaults({
        ...(a.currency === undefined ? {} : { currency: a.currency === 'none' ? null : a.currency.toUpperCase() }),
        ...(a.notional === undefined ? {} : { notional: a.notional === 'none' ? null : parsedValue('leg1.notional', a.notional) as number }),
        ...(a.direction === undefined ? {} : { direction: a.direction === 'none' ? null : parsedValue('direction', a.direction) as string }),
      }),
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

const parsed = (field: string) => (value: string): string | undefined => {
  const result = parserOf(field)!(value);
  return result.ok ? undefined : result.message;
};

function parsedValue(field: string, text: string): unknown {
  const result = parserOf(field)!(text);
  if (!result.ok) throw new Error(`${field} was checked on the line and is refused now: ${result.message}`);
  return result.value;
}

/**
 * `new <product> <terms…>` for each product the server describes (ADR-0008 §1, §5): one generic command with its
 * product named. The words are read in any order; the line completes and shows them from the description, and the
 * server reads them again, fills what its defaults supply and refuses what is missing by name.
 */
function productCommands(products: readonly ProductDescriptor[], on: Partial<Pick<Command, 'unavailable' | 'target'>>, create: (product: string, terms: string) => void): Command[] {
  return products.map((product) => ({
    id: `entity.create/${product.id}`,
    words: `new ${product.words}`,
    title: `Add ${article(product.title)} from short terms, in any order`,
    group: 'Rates',
    ...on,
    args: [{
      name: 'terms', required: true, rest: true, words: true,
      hint: `${product.fields.filter((f) => f.required).map((f) => f.name).join(', ')}; any order, name=value for the rest`,
      complete: (given) => termCompletions(product, wordsOf(given.terms)),
      parts: (text) => termParts(product, wordsOf(text)),
    }],
    run: (a) => create(product.id, a.terms ?? ''),
  }));
}

const wordsOf = (text: string | undefined) => (text ?? '').split(/\s+/).filter((w) => w !== '');
const article = (title: string) => `${/^[aeiou]/i.test(title) ? 'an' : 'a'} ${title.toLowerCase()}`;

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
