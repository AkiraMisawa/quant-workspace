import { useCallback, useEffect, useId, useMemo, useRef, useState, type ReactNode } from 'react';
import './xva.css';
import { Alert, Badge, Button, Group, Modal, Select, Stack, Table, Text, TextInput } from '@mantine/core';
import { GridSource, type CellState, type GridColumn, type GridEditIntent } from '@ex-grid/core';
import type { ExGridApi } from '@ex-grid/react';
import { WorkbenchGrid } from '../../platform/grid/WorkbenchGrid';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError } from '../../platform/api';
import { useWorkspaceContext } from '../../platform/workspace/WorkspaceProvider';
import { keyOf } from '../../platform/workspace/workspaceStore';
import { registerGridFocus } from '../../shell/focus';
import type { XvaApi } from './api';
import type { EffectiveTrade, HeldEdit, Resolution, ScopeDrop, XvaWorkspaceView } from './contracts';
import { registerPrice } from './commands';
import { XvaResult } from './XvaResult';
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

/**
 * Made once: a new column is a new declaration, and every row paints again (ExGrid ADR-0003). Each width holds its
 * header and its widest value: the largest synthetic notional, 10,000,000,000, must not fall to `####`.
 */
const columns: readonly GridColumn<Row>[] = [
  column('tradeId', 'Trade', 'text', 168),
  column('product', 'Product', 'text', 104),
  column('currency', 'Ccy', 'text', 76),
  column('notional', 'Notional', 'number', 168, { editable: true, format: (v) => amount.format(v as number) }),
  column('maturity', 'Maturity', 'text', 112),
  column('fixedRate', 'Fixed rate', 'number', 112, { editable: true, format: (v) => rate.format(v as number) }),
  column('schedule', 'Schedule', 'text', 104, { editable: true }),
  column('lastFixing', 'Last fixing', 'number', 116, { format: (v) => rate.format(v as number) }),
  column('csa', 'CSA', 'text', 120, { editable: true }),
  column('curve', 'Curve', 'text', 112, { editable: true }),
  column('before', 'Before', 'boolean', 88, { editable: true }),
  column('after', 'After', 'boolean', 80, { editable: true }),
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
  const [issue, setIssue] = useState<string | undefined>(undefined);

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

  // The figures of the latest Price stay on the page (XvaResult); the Run itself opens in a panel only when asked.
  const [priced, setPriced] = useState<string | undefined>(undefined);
  const price = async () => {
    setBusy('Pricing');
    try {
      const run = await api.price(workspaceId, queue.revision, crypto.randomUUID());
      setPriced(run.runId);
      void queryClient.invalidateQueries({ queryKey: ['xva', workspaceId] });
      void queryClient.invalidateQueries({ queryKey: ['history'] });
    } catch (error) {
      const targets = error instanceof ApiError && error.envelope.issues.length > 0 ? ` (${error.envelope.issues.map((i) => i.path).join(', ')})` : '';
      workspace.notify(`Price was refused: ${describe(error)}${targets}`);
    } finally {
      setBusy(undefined);
    }
  };

  const openRun = (runId: string) => {
    const resultView = workspace.catalog.views.find((v) => v.definitionId === XVA_RESULT_VIEW);
    workspace.store.getState().openPanel({ kind: 'run', title: `CVA/FVA ${view.data?.registration.key ?? ''}`, runId, viewKey: resultView === undefined ? undefined : keyOf(resultView) });
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

  const pricedRun = priced ?? ws.runs[0]?.runId;
  const pricedRevision = ws.runs.find((r) => r.runId === pricedRun)?.revision;
  const notBusy = busy === undefined;

  return (
    <Stack gap="sm" className="qwb-xva">
      {/* The toolbar (docs/design-guidelines.md §3, §4): one 30px Control Height, each label beside its input. */}
      <div className="qwb-toolbar" role="toolbar" aria-label="Workspace">
        <ToolbarField label="Counterparty" wide>
          {(id) => (
            <Select id={id} size="xs" className="qwb-toolbar-input qwb-toolbar-input--wide" allowDeselect={false} value={ws.registration.key}
              data={(registrations.data ?? [ws.registration]).map((r) => ({ value: r.key, label: `${r.key} · ${r.name}` }))}
              onChange={(value) => void chooseCounterparty(value)} disabled={!notBusy} comboboxProps={{ withinPortal: true }} />
          )}
        </ToolbarField>
        <ValueDate value={ws.context.valueDate} disabled={!notBusy}
          onCommit={(date) => void reacquire('Value date moved', () => api.changeValueDate(workspaceId, queue.revision, date))} />
        <CommitField label="CVA recovery" placeholder={`empty: CDS ${ws.credit.cdsRecovery}`}
          value={ws.credit.cvaRecovery == null ? '' : String(ws.credit.cvaRecovery)} modified={ws.credit.cvaRecoveryOwner === 'user'}
          parse={parseRecovery} onIssue={setIssue}
          onCommit={(value) => void queue.push({ entity: `credit:${ws.registration.key}`, field: 'cvaRecovery', value })} />
        <Button size="xs" onClick={() => void price()} loading={busy === 'Pricing'} disabled={!notBusy && busy !== 'Pricing'}
          {...(blocking > 0 ? { title: `${blocking} held edit(s) on priced trades must be resolved first.` } : {})}>Price</Button>
      </div>
      {ws.csas.length > 0 && (
        <div className="qwb-toolbar" role="toolbar" aria-label="CSA curves">
          {ws.csas.map((c) => (
            <CommitField key={c.csaId} label={`${c.csaId} curve`} value={c.curve} modified={c.curveOwner === 'user'} onIssue={setIssue}
              parse={(t) => (t.trim() === '' ? { ok: false, message: `Type a curve for ${c.csaId}.` } : { ok: true, value: t.trim() })}
              onCommit={(value) => void queue.push({ entity: `csa:${c.csaId}`, field: 'curve', value })} />
          ))}
        </div>
      )}
      <Group gap="xs" wrap="wrap">
        <Badge variant="light" color="gray">{ws.agreement.maId} · {ws.agreement.nettingSetId}</Badge>
        <Badge variant="light" color="gray">{ws.agreement.collateralised ? `main CSA ${ws.agreement.mainCsaId}` : ws.agreement.noCsa}</Badge>
        <Text size="xs" c="dimmed" data-testid="workspace-summary">{ws.trades} trades · {ws.overrides} edit(s) · revision {ws.revision} · {ws.sourceVersion}</Text>
      </Group>
      {issue !== undefined && <Text size="xs" c="red" role="alert" data-testid="field-issue">{issue}</Text>}
      {pricedRun !== undefined && <XvaResult runId={pricedRun} pricedRevision={pricedRevision} revision={ws.revision} onOpen={() => openRun(pricedRun)} />}
      <HeldEdits conflicts={ws.conflicts} lost={ws.lost} resolve={(edit, resolution) => void queue.push({ entity: edit.entity, field: edit.field, resolution })} />
      {failure !== undefined && <Alert color="red" variant="light" withCloseButton onClose={() => setFailure(undefined)} title="The trades could not be read">{failure}</Alert>}
      <div className="qwb-xva-trades" aria-label="Trades">
        <WorkbenchGrid<Row> ref={grid} columns={columns} source={source} onEdit={onEdit} cellState={cellState} cellMessage={cellMessage}
          density="compact" viewportWidth="stretch" viewportHeight="stretch" pinnedColumnCount={1} locale="en-US" />
      </div>
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

/** A toolbar field: its label to the left of its input, both one Control Height (docs/design-guidelines.md §4). */
function ToolbarField({ label, wide, children }: { readonly label: string; readonly wide?: boolean; readonly children: (id: string) => ReactNode }) {
  const id = useId();
  return (
    <div className={wide === true ? 'qwb-toolbar-field qwb-toolbar-field--wide' : 'qwb-toolbar-field'}>
      <Text component="label" htmlFor={id} size="xs" c="dimmed" className="qwb-toolbar-label">{label}</Text>
      {children(id)}
    </div>
  );
}

/**
 * A toolbar field that sends its value on Enter or when left, and only when it changed and reads correctly. A value
 * it cannot read marks the input invalid and is said once below the toolbars, so the row keeps its height.
 */
function CommitField({ label, value, placeholder, modified, parse, onCommit, onIssue }: {
  readonly label: string;
  readonly value: string;
  readonly placeholder?: string;
  readonly modified?: boolean;
  readonly parse: (text: string) => { readonly ok: true; readonly value: unknown } | { readonly ok: false; readonly message: string };
  readonly onCommit: (value: unknown) => void;
  readonly onIssue: (message: string | undefined) => void;
}) {
  const [draft, setDraft] = useState(value);
  const [invalid, setInvalid] = useState(false);
  useEffect(() => setDraft(value), [value]);
  const settle = (message: string | undefined) => { setInvalid(message !== undefined); onIssue(message === undefined ? undefined : `${label}: ${message}`); };
  const commit = () => {
    if (draft === value) { if (invalid) settle(undefined); return; }
    const parsed = parse(draft);
    if (!parsed.ok) { settle(parsed.message); return; }
    settle(undefined);
    onCommit(parsed.value);
  };
  return (
    <ToolbarField label={label}>
      {(id) => (
        <TextInput id={id} size="xs" className="qwb-toolbar-input qwb-toolbar-input--text" value={draft} error={invalid}
          {...(placeholder === undefined ? {} : { placeholder })}
          {...(modified === true ? { 'data-modified': true, title: 'Set by you', classNames: { input: 'qwb-modified' } } : {})}
          onChange={(e) => setDraft(e.currentTarget.value)} onBlur={commit}
          onKeyDown={(e) => { if (e.key === 'Enter') commit(); if (e.key === 'Escape') { setDraft(value); if (invalid) settle(undefined); } }} />
      )}
    </ToolbarField>
  );
}

function ValueDate({ value, disabled, onCommit }: { readonly value: string; readonly disabled: boolean; readonly onCommit: (date: string) => void }) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  const commit = () => { if (draft !== value && /^\d{4}-\d{2}-\d{2}$/.test(draft)) onCommit(draft); };
  return (
    <ToolbarField label="Value date">
      {(id) => (
        <TextInput id={id} type="date" size="xs" className="qwb-toolbar-input qwb-toolbar-input--date" value={draft} disabled={disabled}
          onChange={(e) => setDraft(e.currentTarget.value)} onBlur={commit} onKeyDown={(e) => { if (e.key === 'Enter') commit(); }} />
      )}
    </ToolbarField>
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
