import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Badge, Button, Group, Modal, Select, Stack, Table, Text, TextInput } from '@mantine/core';
import { GridSource, type CellState, type GridColumn, type GridEditIntent } from '@ex-grid/core';
import { ExGrid, type ExGridApi } from '@ex-grid/react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError } from '../../platform/api';
import { useWorkspaceContext } from '../../platform/workspace/WorkspaceProvider';
import { keyOf } from '../../platform/workspace/workspaceStore';
import { registerGridFocus } from '../../shell/focus';
import type { XvaApi } from './api';
import type { EffectiveTrade, HeldEdit, Resolution, ScopeDrop, XvaWorkspaceView } from './contracts';
import { registerPrice } from './commands';
import { EditQueue, parseRecovery, tradeFields, type PendingEdit } from './editing';

// The fixed XVA workspace (docs/03, ADR-0003): the server holds the trades and every rule; this page shows
// the effective rows a page at a time and sends what the user changed. ExGrid draws; it decides nothing.

export const XVA_RESULT_VIEW = 'view.xva.result';

const amount = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 });
const rate = new Intl.NumberFormat('en-US', { minimumFractionDigits: 5, maximumFractionDigits: 5 });
const text = (v: unknown) => String(v);

type Row = EffectiveTrade;

const column = (name: keyof Row & string, header: string, type: GridColumn<Row>['type'], width: number, extra: Partial<GridColumn<Row>> = {}): GridColumn<Row> => ({
  name, header, type, width, value: (row) => row[name] ?? undefined, ...extra,
});

/** Made once: a new column is a new declaration, and every row paints again (ExGrid ADR-0003). */
const columns: readonly GridColumn<Row>[] = [
  column('tradeId', 'Trade', 'text', 150),
  column('product', 'Product', 'text', 90),
  column('currency', 'Ccy', 'text', 56),
  column('notional', 'Notional', 'number', 140, { editable: true, format: (v) => amount.format(v as number) }),
  column('maturity', 'Maturity', 'text', 100),
  column('fixedRate', 'Fixed rate', 'number', 96, { editable: true, format: (v) => rate.format(v as number) }),
  column('schedule', 'Schedule', 'text', 80, { editable: true }),
  column('lastFixing', 'Last fixing', 'number', 96, { format: (v) => rate.format(v as number) }),
  column('csa', 'CSA', 'text', 130, { editable: true }),
  column('curve', 'Curve', 'text', 100, { editable: true }),
  column('before', 'Before', 'boolean', 72, { editable: true }),
  column('after', 'After', 'boolean', 72, { editable: true }),
  { name: 'issues', header: 'Issues', type: 'text', width: 260, value: (row) => (row.issues.length === 0 ? undefined : row.issues.join(' ')), format: text },
];

function cellState(row: Row, col: GridColumn<Row>): CellState {
  if (row.held.some((h) => h.startsWith(`${col.name}:`))) return 'stale';
  if ((col.name === 'csa' || col.name === 'curve' || col.name === 'issues') && row.issues.length > 0) return 'error';
  if (row.overridden.includes(col.name)) return 'modified';
  return 'normal';
}

function cellMessage(row: Row, col: GridColumn<Row>): string | undefined {
  const held = row.held.find((h) => h.startsWith(`${col.name}:`));
  if (held !== undefined) return held.endsWith('conflict')
    ? 'The source changed under your edit. Keep it or take the source value in the panel above.'
    : 'This edit lost its trade in the new version. Acknowledge or reset it in the panel above.';
  if ((col.name === 'csa' || col.name === 'curve' || col.name === 'issues') && row.issues.length > 0) return row.issues.join(' ');
  if (row.overridden.includes(col.name)) return 'Set by you.';
  return undefined;
}

const describe = (error: unknown) => (error instanceof Error ? error.message : String(error));

export function XvaWorkspacePanel({ panelId, workspaceId, api }: { readonly panelId: string; readonly workspaceId: string; readonly api: XvaApi }) {
  const workspace = useWorkspaceContext();
  const queryClient = useQueryClient();
  const view = useQuery({ queryKey: ['xva', workspaceId], queryFn: () => api.view(workspaceId) });
  const registrations = useQuery({ queryKey: ['xva', 'registrations'], queryFn: () => api.registrations(), staleTime: Infinity });
  const grid = useRef<ExGridApi<Row>>(null);
  const [busy, setBusy] = useState<string | undefined>(undefined);
  const [confirm, setConfirm] = useState<{ readonly registration: string; readonly drop: ScopeDrop } | undefined>(undefined);
  const [failure, setFailure] = useState<string | undefined>(undefined);

  const source = useMemo(() => GridSource.fetch<Row>(async (query, signal) => {
    // Filtering is not offered by the server yet; a filter set anyway is refused, never ignored.
    if (query.filter !== undefined) throw new Error('This grid cannot filter yet. Clear the filter to see the trades.');
    const page = await api.rows(workspaceId, { start: query.range.start, count: query.range.count, sorts: query.sorts }, signal);
    return { rows: page.rows, start: page.start, totalCount: page.totalCount, orderToken: page.orderToken };
  }, { rowKey: (row) => row.tradeId, onFetchFailed: (event) => setFailure(describe(event.error)) }), [api, workspaceId]);
  useEffect(() => () => source.dispose(), [source]);

  const show = useCallback((next: XvaWorkspaceView) => queryClient.setQueryData(['xva', workspaceId], next), [queryClient, workspaceId]);

  const queue = useMemo(() => new EditQueue(0,
    (revision, edit: PendingEdit) => edit.resolution === undefined
      ? api.edit(workspaceId, revision, edit.entity, edit.field, edit.value)
      : api.resolve(workspaceId, revision, edit.entity, edit.field, edit.resolution),
    (next) => { show(next); source.notifyChanged([]); },
    (edit, error) => {
      workspace.notify(`${edit.entity} ${edit.field} was not changed: ${describe(error)}`);
      void api.view(workspaceId).then((fresh) => { queue.rebase(fresh.revision); show(fresh); source.notifyChanged([]); });
    }), [api, workspaceId, show, source, workspace]);
  // The queue starts from the revision the page first read, and follows every command that is not an edit.
  const first = view.data?.revision;
  useEffect(() => { if (first !== undefined && queue.revision === 0) queue.rebase(first); }, [first, queue]);

  const onEdit = useCallback((intent: GridEditIntent<Row>) => {
    const parse = tradeFields[intent.column];
    if (parse === undefined) { intent.refuse('This column is not editable.'); return; }
    if (intent.seenText !== intent.replacedText) { intent.refuse('The value changed while you were editing. Look at it again.'); return; }
    const parsed = parse(intent.value);
    if (!parsed.ok) { intent.refuse(parsed.message); return; }
    void queue.push({ entity: `trade:${intent.row.tradeId}`, field: intent.column, value: parsed.value });
  }, [queue]);

  useEffect(() => {
    registerGridFocus(panelId, () => grid.current?.focus());
    return () => registerGridFocus(panelId, undefined);
  }, [panelId]);

  /** A command that re-reads the source: the order may move, so the grid starts over (ExGrid ADR-0011). */
  const reacquire = async (label: string, run: () => Promise<{ readonly workspace: XvaWorkspaceView; readonly carried: { readonly carried: number; readonly conflicted: number; readonly lost: number } }>) => {
    setBusy(label);
    try {
      const { workspace: next, carried } = await run();
      queue.rebase(next.revision);
      show(next);
      source.requery();
      workspace.notify(`${label}: ${carried.carried} edit(s) carried, ${carried.conflicted} in conflict, ${carried.lost} lost.`);
    } catch (error) {
      workspace.notify(`${label} failed: ${describe(error)}`);
      const fresh = await api.view(workspaceId);
      queue.rebase(fresh.revision);
      show(fresh);
    } finally {
      setBusy(undefined);
    }
  };

  const chooseCounterparty = async (registration: string | null) => {
    if (registration === null || view.data === undefined || registration === view.data.registration.key) return;
    const drop = await api.previewCounterpartyChange(workspaceId);
    if (drop.count === 0) await reacquire('Counterparty changed', () => api.changeCounterparty(workspaceId, queue.revision, registration, 0));
    else setConfirm({ registration, drop });
  };

  const price = async () => {
    setBusy('Pricing');
    try {
      const run = await api.price(workspaceId, queue.revision, crypto.randomUUID());
      const resultView = workspace.catalog.views.find((v) => v.definitionId === XVA_RESULT_VIEW);
      workspace.store.getState().openPanel({ kind: 'run', title: `CVA/FVA ${view.data?.registration.key ?? ''}`, runId: run.runId, viewKey: resultView === undefined ? undefined : keyOf(resultView) });
      void queryClient.invalidateQueries({ queryKey: ['xva', workspaceId] });
      void queryClient.invalidateQueries({ queryKey: ['history'] });
    } catch (error) {
      const targets = error instanceof ApiError && error.envelope.issues.length > 0 ? ` (${error.envelope.issues.map((i) => i.path).join(', ')})` : '';
      workspace.notify(`Price was refused: ${describe(error)}${targets}`);
    } finally {
      setBusy(undefined);
    }
  };

  const priceRef = useRef(price);
  priceRef.current = price;
  useEffect(() => {
    registerPrice(panelId, () => void priceRef.current());
    return () => registerPrice(panelId, undefined);
  }, [panelId]);

  if (view.isPending) return <Text c="dimmed">Loading workspace…</Text>;
  if (view.isError) return <Alert color="red" title="Workspace">{view.error.message}</Alert>;
  const ws = view.data;
  const blocking = ws.conflicts.length + ws.lost.filter((l) => !l.acknowledged).length;

  return (
    <Stack gap="sm">
      <Group gap="sm" wrap="wrap" align="flex-end">
        <Select label="Counterparty" size="xs" w={240} allowDeselect={false} value={ws.registration.key}
          data={(registrations.data ?? [ws.registration]).map((r) => ({ value: r.key, label: `${r.key} · ${r.name}` }))}
          onChange={(value) => void chooseCounterparty(value)} disabled={busy !== undefined} />
        <ValueDate value={ws.context.valueDate} disabled={busy !== undefined}
          onCommit={(date) => void reacquire('Value date moved', () => api.changeValueDate(workspaceId, queue.revision, date))} />
        <CommitField label="CVA recovery" placeholder={`empty: CDS ${ws.credit.cdsRecovery}`} value={ws.credit.cvaRecovery === null ? '' : String(ws.credit.cvaRecovery)}
          parse={parseRecovery} onCommit={(value) => void queue.push({ entity: `credit:${ws.registration.key}`, field: 'cvaRecovery', value })} />
        <Button size="xs" onClick={() => void price()} loading={busy === 'Pricing'} disabled={busy !== undefined && busy !== 'Pricing'}
          {...(blocking > 0 ? { title: `${blocking} held edit(s) on priced trades must be resolved first.` } : {})}>Price</Button>
      </Group>
      <Group gap="xs" wrap="wrap">
        <Badge variant="light">{ws.agreement.maId} · {ws.agreement.nettingSetId}</Badge>
        <Badge variant="light" color={ws.agreement.collateralised ? 'blue' : 'gray'}>{ws.agreement.collateralised ? `main CSA ${ws.agreement.mainCsaId}` : ws.agreement.noCsa}</Badge>
        <Text size="xs" c="dimmed" data-testid="workspace-summary">{ws.trades} trades · {ws.overrides} edit(s) · revision {ws.revision} · {ws.sourceVersion}</Text>
      </Group>
      {ws.csas.length > 0 && (
        <Group gap="sm" wrap="wrap">
          {ws.csas.map((c) => (
            <CommitField key={c.csaId} label={`${c.csaId} curve`} value={c.curve} modified={c.curveOwner === 'user'}
              parse={(t) => (t.trim() === '' ? { ok: false, message: 'Type a curve.' } : { ok: true, value: t.trim() })}
              onCommit={(value) => void queue.push({ entity: `csa:${c.csaId}`, field: 'curve', value })} />
          ))}
        </Group>
      )}
      <HeldEdits conflicts={ws.conflicts} lost={ws.lost} resolve={(edit, resolution) => void queue.push({ entity: edit.entity, field: edit.field, resolution })} />
      {failure !== undefined && <Alert color="red" variant="light" withCloseButton onClose={() => setFailure(undefined)} title="The trades could not be read">{failure}</Alert>}
      <div className="qwb-grid" aria-label="Trades">
        <ExGrid<Row> ref={grid} columns={columns} source={source} onEdit={onEdit} cellState={cellState} cellMessage={cellMessage}
          density="compact" viewportWidth="stretch" viewportHeight={420} pinnedColumnCount={1} locale="en-US" />
      </div>
      {ws.runs.length > 0 && <Text size="xs" c="dimmed">Priced: {ws.runs.map((r) => `${r.runId.slice(-8)} (r${r.revision})`).join(', ')}</Text>}
      <Modal opened={confirm !== undefined} onClose={() => setConfirm(undefined)} title="Change counterparty" trapFocus transitionProps={{ duration: 0 }}>
        {confirm !== undefined && (
          <Stack gap="sm">
            <Text size="sm">Moving to {confirm.registration} drops {confirm.drop.count} edit(s) made for {ws.registration.key}. They stay in the log and in any Run that priced them; nothing brings them back by itself.</Text>
            <Table withTableBorder={false}><Table.Tbody>
              {Object.entries(confirm.drop.byKind).map(([kind, count]) => <Table.Tr key={kind}><Table.Td>{kind}</Table.Td><Table.Td>{count}</Table.Td></Table.Tr>)}
            </Table.Tbody></Table>
            <Group justify="flex-end">
              <Button variant="default" onClick={() => setConfirm(undefined)}>Keep {ws.registration.key}</Button>
              <Button data-autofocus color="red" onClick={() => {
                const { registration, drop } = confirm;
                setConfirm(undefined);
                void reacquire('Counterparty changed', () => api.changeCounterparty(workspaceId, queue.revision, registration, drop.count));
              }}>Drop {confirm.drop.count} and change</Button>
            </Group>
          </Stack>
        )}
      </Modal>
    </Stack>
  );
}

/** A field that sends its value on Enter or when left, and only when it changed and reads correctly. */
function CommitField({ label, value, placeholder, modified, parse, onCommit }: {
  readonly label: string;
  readonly value: string;
  readonly placeholder?: string;
  readonly modified?: boolean;
  readonly parse: (text: string) => { readonly ok: true; readonly value: unknown } | { readonly ok: false; readonly message: string };
  readonly onCommit: (value: unknown) => void;
}) {
  const [draft, setDraft] = useState(value);
  const [error, setError] = useState<string | undefined>(undefined);
  useEffect(() => setDraft(value), [value]);
  const commit = () => {
    if (draft === value) { setError(undefined); return; }
    const parsed = parse(draft);
    if (!parsed.ok) { setError(parsed.message); return; }
    setError(undefined);
    onCommit(parsed.value);
  };
  return (
    <TextInput label={label} size="xs" w={150} value={draft} error={error}
      {...(placeholder === undefined ? {} : { placeholder })}
      {...(modified === true ? { styles: { input: { fontWeight: 600 } }, description: 'set by you' } : {})}
      onChange={(e) => setDraft(e.currentTarget.value)} onBlur={commit}
      onKeyDown={(e) => { if (e.key === 'Enter') commit(); if (e.key === 'Escape') { setDraft(value); setError(undefined); } }} />
  );
}

function ValueDate({ value, disabled, onCommit }: { readonly value: string; readonly disabled: boolean; readonly onCommit: (date: string) => void }) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  const commit = () => { if (draft !== value && /^\d{4}-\d{2}-\d{2}$/.test(draft)) onCommit(draft); };
  return (
    <TextInput label="Value date" type="date" size="xs" w={150} value={draft} disabled={disabled}
      onChange={(e) => setDraft(e.currentTarget.value)} onBlur={commit} onKeyDown={(e) => { if (e.key === 'Enter') commit(); }} />
  );
}

function HeldEdits({ conflicts, lost, resolve }: {
  readonly conflicts: readonly HeldEdit[];
  readonly lost: readonly HeldEdit[];
  readonly resolve: (edit: HeldEdit, resolution: Resolution) => void;
}) {
  if (conflicts.length === 0 && lost.length === 0) return null;
  const value = (v: unknown) => (v === null || v === undefined ? 'empty' : String(v));
  return (
    <Alert color="yellow" variant="light" title={`${conflicts.length} conflict(s), ${lost.length} lost edit(s)`} data-testid="held-edits">
      <Stack gap={4}>
        {conflicts.map((c) => (
          <Group key={`${c.entity}.${c.field}`} gap="xs" wrap="wrap">
            <Text size="xs" ff="monospace">{c.entity} {c.field}</Text>
            <Text size="xs">yours {value(c.value)}, source now {Object.entries(c.source ?? {}).map(([f, v]) => `${f} ${value(v)}`).join(', ')}</Text>
            <Button size="compact-xs" variant="default" onClick={() => resolve(c, 'keep')}>Keep mine</Button>
            <Button size="compact-xs" variant="default" onClick={() => resolve(c, 'takeSource')}>Take source</Button>
          </Group>
        ))}
        {lost.map((l) => (
          <Group key={`${l.entity}.${l.field}`} gap="xs" wrap="wrap">
            <Text size="xs" ff="monospace">{l.entity} {l.field}</Text>
            <Text size="xs">{value(l.value)}: the trade is not in this version{l.acknowledged ? ' (seen)' : ''}</Text>
            {!l.acknowledged && <Button size="compact-xs" variant="default" onClick={() => resolve(l, 'acknowledge')}>Seen</Button>}
            <Button size="compact-xs" variant="default" onClick={() => resolve(l, 'reset')}>Discard</Button>
          </Group>
        ))}
      </Stack>
    </Alert>
  );
}
