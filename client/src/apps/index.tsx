import { useState, type ReactNode } from 'react';
import { Button, Group, Modal, NumberInput, Select, Stack, TextInput } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import type { Command } from '../platform/commands/commands';
import { useWorkspace, useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import type { Panel, WorkspaceState } from '../platform/workspace/workspaceStore';
import { PricingDbPanel } from './history/PricingDbPanel';
import { httpXvaApi, type XvaApi } from './xva/api';
import { priceOf } from './xva/commands';
import { XvaWorkspacePanel } from './xva/XvaWorkspacePanel';

// The fixed applications (docs/01): pages a domain offers beside the composed workflows. Each is reached
// through commands, so the palette, a shortcut and a button cannot differ.

export const XVA_WORKSPACE = 'xva.workspace';
export const PRICING_DB = 'history.pricingDb';
const NEW_XVA = 'xva.new';

let xvaApi: XvaApi = httpXvaApi;
/** For tests: the XVA endpoints a page talks to. */
export function setXvaApi(api: XvaApi): void {
  xvaApi = api;
}

export function AppPanel({ panel }: { readonly panel: Extract<Panel, { kind: 'app' }> }): ReactNode {
  switch (panel.app) {
    case XVA_WORKSPACE: {
      const id = panel.params.workspaceId;
      if (id === undefined) throw new Error('An XVA workspace panel names its workspace.');
      return <XvaWorkspacePanel panelId={panel.id} workspaceId={id} api={xvaApi} />;
    }
    case PRICING_DB:
      return <PricingDbPanel panelId={panel.id} />;
    default:
      throw new Error(`No app draws ${panel.app}.`);
  }
}

export function appCommands(state: Pick<WorkspaceState, 'showApp' | 'openAppDialog' | 'panels' | 'activePanelId'>): Command[] {
  const active = state.panels.find((p) => p.id === state.activePanelId);
  // Looked up when the command runs: the page registers its Price once it has mounted, after this list is made.
  const xva = active?.kind === 'app' && active.app === XVA_WORKSPACE ? active.id : undefined;
  return [
    {
      id: 'xva.new',
      title: 'New XVA workspace',
      group: 'XVA',
      keywords: ['cva', 'fva', 'pricer', 'counterparty'],
      shortcut: 'mod+alt+N',
      run: () => state.openAppDialog(NEW_XVA),
    },
    {
      id: 'xva.price',
      title: 'Price XVA workspace',
      group: 'XVA',
      keywords: ['cva', 'fva', 'run'],
      shortcut: 'mod+alt+P',
      ...(xva === undefined ? { unavailable: 'No XVA workspace is active.' } : {}),
      run: () => {
        const price = priceOf(xva);
        if (price === undefined) throw new Error(`The XVA workspace in panel ${xva ?? '(none)'} registered no Price.`);
        price();
      },
    },
    {
      id: 'history.open',
      title: 'Open Pricing DB',
      group: 'History',
      keywords: ['runs', 'history', 'pricing'],
      shortcut: 'mod+alt+H',
      run: () => state.showApp(PRICING_DB, 'Pricing DB', {}),
    },
  ];
}

/** The dialogs apps open. Enter submits; Escape closes. */
export function AppDialogs() {
  const dialog = useWorkspace((s) => s.appDialog);
  const close = useWorkspace((s) => s.openAppDialog);
  return (
    <Modal opened={dialog === NEW_XVA} onClose={() => close(undefined)} title="New XVA workspace" trapFocus returnFocus transitionProps={{ duration: 0 }}>
      {dialog === NEW_XVA && <NewXvaWorkspace onDone={() => close(undefined)} />}
    </Modal>
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
