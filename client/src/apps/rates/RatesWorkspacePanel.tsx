import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Badge, Button, Group, Stack, Table, Text, TextInput } from '@mantine/core';
import { GridSource, type GridActionEvent, type GridColumn, type GridEditIntent } from '@ex-grid/core';
import { ExGrid, type ExGridApi } from '@ex-grid/react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError } from '../../platform/api';
import { EditQueue } from '../../platform/workspace/editQueue';
import { useWorkspaceContext } from '../../platform/workspace/WorkspaceProvider';
import { keyOf } from '../../platform/workspace/workspaceStore';
import { registerGridFocus } from '../../shell/focus';
import type { FrameStream, RatesApi } from './api';
import { registerRatesCommands } from './commands';
import type { CashflowsView, HeldEdit, LiveFrame, PeriodView, RatesWorkspaceView, Resolution } from './contracts';
import {
  PINNED_COLUMNS, lineCellMessage, lineCellState, lineColumns, lineHeaderGroups, lineRows, parserOf, periodCellState, periodColumns, periodParserOf,
  type LineRow,
} from './lines';

// The fixed Rates line pricer (docs/07, C37 to C43): one line per trade in ExGrid, its terms sparse, its
// standard cashflows generated and customised on the server. Live frames tick the figures; Price and Capture
// make Runs. The server holds every rule; this page sends what the user changed and shows what came back.

export const RATES_RESULT_VIEW = 'view.rates.result';

type Edit =
  | { readonly kind: 'line'; readonly line: string; readonly field: string; readonly value: unknown }
  | { readonly kind: 'cashflow'; readonly line: string; readonly period: string; readonly field: string; readonly value: unknown }
  | { readonly kind: 'resolve'; readonly held: HeldEdit; readonly resolution: Resolution }
  | { readonly kind: 'add'; readonly product: 'irs' | 'ccs' }
  | { readonly kind: 'remove'; readonly line: string };

const describe = (error: unknown) => (error instanceof Error ? error.message : String(error));

/** The four column sets, made once each: a new column is a new declaration, and every row paints again (ExGrid ADR-0003). */
const layouts = new Map<string, { readonly columns: ReturnType<typeof lineColumns>; readonly groups: ReturnType<typeof lineHeaderGroups> }>();
function layoutOf(leg1: boolean, leg2: boolean) {
  const key = `${leg1}/${leg2}`;
  let layout = layouts.get(key);
  if (layout === undefined) {
    layout = { columns: lineColumns({ leg1, leg2 }), groups: lineHeaderGroups({ leg1, leg2 }) };
    layouts.set(key, layout);
  }
  return layout;
}

export function RatesWorkspacePanel({ panelId, workspaceId, api }: { readonly panelId: string; readonly workspaceId: string; readonly api: RatesApi }) {
  const workspace = useWorkspaceContext();
  const queryClient = useQueryClient();
  const view = useQuery({ queryKey: ['rates', workspaceId], queryFn: () => api.view(workspaceId) });
  const market = useQuery({ queryKey: ['rates', 'market'], queryFn: () => api.market(), refetchInterval: 2000 });
  const grid = useRef<ExGridApi<LineRow>>(null);
  const [expanded, setExpanded] = useState({ leg1: false, leg2: false });
  const [frame, setFrame] = useState<LiveFrame | undefined>(undefined);
  const [subscription, setSubscription] = useState<string | undefined>(undefined);
  const [detail, setDetail] = useState<string | undefined>(undefined);
  const [busy, setBusy] = useState<string | undefined>(undefined);

  const show = useCallback((next: RatesWorkspaceView) => queryClient.setQueryData(['rates', workspaceId], next), [queryClient, workspaceId]);

  const queue = useMemo(() => new EditQueue<Edit, RatesWorkspaceView>(0,
    async (revision, edit) => {
      switch (edit.kind) {
        case 'line': return api.editLine(workspaceId, revision, edit.line, edit.field, edit.value);
        case 'cashflow': return api.editCashflow(workspaceId, revision, edit.line, edit.period, edit.field, edit.value);
        case 'resolve': return api.resolve(workspaceId, revision, edit.held.scope, edit.held.entity, edit.held.field, edit.resolution);
        case 'add': return (await api.addLine(workspaceId, revision, edit.product)).workspace;
        case 'remove': return api.removeLine(workspaceId, revision, edit.line);
      }
    },
    (next) => { show(next); void queryClient.invalidateQueries({ queryKey: ['rates', workspaceId, 'cashflows'] }); },
    (edit, error) => {
      workspace.notify(`${edit.kind === 'line' || edit.kind === 'cashflow' ? `${edit.line} ${edit.field}` : edit.kind} was not changed: ${describe(error)}`);
      void api.view(workspaceId).then((fresh) => { queue.rebase(fresh.revision); show(fresh); });
    }), [api, workspaceId, show, workspace, queryClient]);
  const first = view.data?.revision;
  useEffect(() => { if (first !== undefined && queue.revision === 0) queue.rebase(first); }, [first, queue]);

  // The rows live in a source keyed by line, so a tick replaces only the rows whose figures moved.
  const source = useMemo(() => GridSource.from<LineRow>([], { rowKey: (row) => row.line }), []);
  const rowsByLine = useRef<ReadonlyMap<string, LineRow>>(new Map());
  useEffect(() => {
    if (view.data === undefined) return;
    const rows = lineRows(view.data.lines, view.data.revision, frame, rowsByLine.current);
    rowsByLine.current = new Map(rows.map((r) => [r.line, r]));
    source.replaceAll(rows);
  }, [view.data, frame, source]);

  // Live: one stream per subscription; stopped when the page goes, so nothing ticks for nobody (docs/07 §9.3).
  const stream = useRef<FrameStream | undefined>(undefined);
  const stopLive = useCallback(async (id: string | undefined) => {
    stream.current?.close();
    stream.current = undefined;
    setSubscription(undefined);
    if (id !== undefined) await api.stopLive(id);
  }, [api]);
  const startLive = useCallback(async () => {
    const { subscriptionId } = await api.startLive(workspaceId);
    setSubscription(subscriptionId);
    stream.current = api.frames(subscriptionId, setFrame, () => {
      workspace.notify('The live stream broke; ticking stopped. Start it again to resume.');
      void stopLive(subscriptionId);
    });
  }, [api, workspaceId, workspace, stopLive]);
  const subscriptionRef = useRef(subscription);
  subscriptionRef.current = subscription;
  useEffect(() => () => { void stopLive(subscriptionRef.current); }, [stopLive]);

  const onEdit = useCallback((intent: GridEditIntent<LineRow>) => {
    const parse = parserOf(intent.column);
    if (parse === undefined) { intent.refuse('This column is not editable.'); return; }
    if (intent.seenText !== intent.replacedText) { intent.refuse('The value changed while you were editing. Look at it again.'); return; }
    const parsed = parse(intent.value);
    if (!parsed.ok) { intent.refuse(parsed.message); return; }
    void queue.push({ kind: 'line', line: intent.row.line, field: intent.column, value: parsed.value });
  }, [queue]);

  const onAction = useCallback((event: GridActionEvent<LineRow>) => {
    if (event.action === 'cashflows') setDetail(event.row.line);
    else if (event.action === 'remove') {
      if (detail === event.row.line) setDetail(undefined);
      void queue.push({ kind: 'remove', line: event.row.line });
    } else throw new Error(`The grid reported an action it was not given: ${event.action}.`);
  }, [queue, detail]);

  const price = async () => {
    const snapshot = market.data?.snapshotId;
    if (snapshot === undefined) { workspace.notify('No market snapshot has been published yet; Price waits for one.'); return; }
    setBusy('Pricing');
    try {
      const run = await api.price(workspaceId, queue.revision, crypto.randomUUID(), snapshot);
      openRun(run.runId, `Rates ${snapshot.split(':').at(-1) ?? ''}`);
    } catch (error) {
      workspace.notify(`Price was refused: ${describe(error)}${targetsOf(error)}`);
    } finally {
      setBusy(undefined);
    }
  };

  const capture = async () => {
    if (subscription === undefined || frame === undefined) { workspace.notify('Capture needs a live frame on screen.'); return; }
    setBusy('Capturing');
    try {
      const run = await api.capture(subscription, frame, crypto.randomUUID());
      openRun(run.runId, `Capture ${frame.market.snapshotId.split(':').at(-1) ?? ''}`);
    } catch (error) {
      workspace.notify(`Capture was refused: ${describe(error)}`);
    } finally {
      setBusy(undefined);
    }
  };

  const openRun = (runId: string, title: string) => {
    const resultView = workspace.catalog.views.find((v) => v.definitionId === RATES_RESULT_VIEW);
    workspace.store.getState().openPanel({ kind: 'run', title, runId, viewKey: resultView === undefined ? undefined : keyOf(resultView) });
    void queryClient.invalidateQueries({ queryKey: ['rates', workspaceId] });
    void queryClient.invalidateQueries({ queryKey: ['history'] });
  };

  const changeValuationDate = async (date: string) => {
    setBusy('Moving the valuation date');
    try {
      const { workspace: next, carried } = await api.changeValuationDate(workspaceId, queue.revision, date);
      queue.rebase(next.revision);
      show(next);
      workspace.notify(`Valuation date moved: ${carried.carried} cashflow edit(s) carried, ${carried.conflicted} in conflict, ${carried.lost} lost.`);
    } catch (error) {
      workspace.notify(`The valuation date was not moved: ${describe(error)}`);
    } finally {
      setBusy(undefined);
    }
  };

  // The page's commands run the same functions as its buttons.
  const commands = useRef({ price, live: () => (subscription === undefined ? startLive() : stopLive(subscription)), capture });
  commands.current = { price, live: () => (subscription === undefined ? startLive() : stopLive(subscription)), capture };
  useEffect(() => {
    registerRatesCommands(panelId, {
      price: () => void commands.current.price(),
      live: () => void commands.current.live(),
      capture: () => void commands.current.capture(),
    });
    registerGridFocus(panelId, () => grid.current?.focus());
    return () => { registerRatesCommands(panelId, undefined); registerGridFocus(panelId, undefined); };
  }, [panelId]);

  if (view.isPending) return <Text c="dimmed">Loading workspace…</Text>;
  if (view.isError) return <Alert color="red" title="Workspace">{view.error.message}</Alert>;
  const ws = view.data;
  const layout = layoutOf(expanded.leg1, expanded.leg2);
  const height = Math.min(64 + Math.max(ws.lines.length, 3) * 28, 420);

  return (
    <Stack gap="sm">
      <Group gap="sm" wrap="wrap" align="flex-end">
        <ValuationDate value={ws.valuationDate} disabled={busy !== undefined} onCommit={(d) => void changeValuationDate(d)} />
        <Button size="xs" variant="default" onClick={() => void queue.push({ kind: 'add', product: 'irs' })}>Add IRS</Button>
        <Button size="xs" variant="default" onClick={() => void queue.push({ kind: 'add', product: 'ccs' })}>Add CCS</Button>
        <Button size="xs" variant={expanded.leg1 ? 'filled' : 'default'} aria-pressed={expanded.leg1}
          onClick={() => setExpanded((e) => ({ ...e, leg1: !e.leg1 }))}>Leg 1 details</Button>
        <Button size="xs" variant={expanded.leg2 ? 'filled' : 'default'} aria-pressed={expanded.leg2}
          onClick={() => setExpanded((e) => ({ ...e, leg2: !e.leg2 }))}>Leg 2 details</Button>
        <Button size="xs" onClick={() => void price()} loading={busy === 'Pricing'} disabled={busy !== undefined && busy !== 'Pricing'}>Price</Button>
        <Button size="xs" color={subscription === undefined ? 'teal' : 'red'} variant="light" data-testid="live-toggle"
          onClick={() => void (subscription === undefined ? startLive() : stopLive(subscription))}>
          {subscription === undefined ? 'Start live' : 'Stop live'}
        </Button>
        <Button size="xs" variant="default" disabled={subscription === undefined || frame === undefined} loading={busy === 'Capturing'}
          onClick={() => void capture()}>Capture</Button>
      </Group>
      <Group gap="xs" wrap="wrap">
        <Badge variant="light" color="gray" data-testid="market">
          {market.data === undefined ? 'no market yet' : `market ${market.data.origin} #${market.data.sequence}`}
        </Badge>
        {frame !== undefined && (
          <Badge variant="light" color={frame.inputRevision < ws.revision ? 'yellow' : 'teal'} data-testid="live-frame">
            {subscription === undefined ? 'last frame' : 'live'} · r{frame.inputRevision} · #{frame.market.sequence} · {frame.calculationMode}
          </Badge>
        )}
        <Text size="xs" c="dimmed" data-testid="workspace-summary">{ws.lines.length} line(s) · revision {ws.revision}</Text>
      </Group>
      <HeldEdits conflicts={ws.conflicts} lost={ws.lost} resolve={(held, resolution) => void queue.push({ kind: 'resolve', held, resolution })} />
      <div className="qwb-grid" aria-label="Lines">
        <ExGrid<LineRow> ref={grid} columns={layout.columns} headerGroups={layout.groups} source={source} onEdit={onEdit} onAction={onAction}
          cellState={lineCellState} cellMessage={lineCellMessage} density="compact" viewportWidth="stretch" viewportHeight={height}
          pinnedColumnCount={PINNED_COLUMNS} locale="en-US" />
      </div>
      {detail !== undefined && ws.lines.some((l) => l.line === detail) && (
        <Cashflows workspaceId={workspaceId} line={detail} revision={ws.revision} api={api} onClose={() => setDetail(undefined)}
          edit={(period, field, value) => void queue.push({ kind: 'cashflow', line: detail, period, field, value })} />
      )}
      {ws.runs.length > 0 && <Text size="xs" c="dimmed">Runs: {ws.runs.map((r) => `${r.runId.slice(-8)} (r${r.revision})`).join(', ')}</Text>}
    </Stack>
  );
}

const targetsOf = (error: unknown) =>
  error instanceof ApiError && error.envelope.issues.length > 0 ? ` (${error.envelope.issues.map((i) => i.path).join(', ')})` : '';

/** A line's cashflows: its generated periods with the user's edits on them, and its notional exchanges. */
function Cashflows({ workspaceId, line, revision, api, edit, onClose }: {
  readonly workspaceId: string;
  readonly line: string;
  readonly revision: number;
  readonly api: RatesApi;
  readonly edit: (period: string, field: string, value: unknown) => void;
  readonly onClose: () => void;
}) {
  const flows = useQuery({ queryKey: ['rates', workspaceId, 'cashflows', line, revision], queryFn: () => api.cashflows(workspaceId, line) });
  const source = useMemo(() => GridSource.from<PeriodView>([], { rowKey: (p) => p.entity }), []);
  useEffect(() => { if (flows.data !== undefined) source.replaceAll(flows.data.periods); }, [flows.data, source]);

  const onEdit = useCallback((intent: GridEditIntent<PeriodView>) => {
    const parse = periodParserOf(intent.column);
    if (parse === undefined) { intent.refuse('This column is not editable.'); return; }
    if (intent.seenText !== intent.replacedText) { intent.refuse('The value changed while you were editing. Look at it again.'); return; }
    const parsed = parse(intent.value);
    if (!parsed.ok) { intent.refuse(parsed.message); return; }
    edit(intent.row.entity, intent.column, parsed.value);
  }, [edit]);

  const cellMessage = useCallback((p: PeriodView, c: GridColumn<PeriodView>) => (p.userFields.includes(c.name) ? 'Set by you; carried while this period exists.' : undefined), []);

  return (
    <Stack gap={6} data-testid="cashflows">
      <Group justify="space-between">
        <Text size="sm" fw={600}>Cashflows of {line}</Text>
        <Button size="compact-xs" variant="default" onClick={onClose}>Close</Button>
      </Group>
      {flows.isError && <Alert color="red" variant="light">{flows.error.message}</Alert>}
      {flows.data !== undefined && flows.data.periods.length === 0 && (
        <Text size="xs" c="dimmed">This line generates no schedule until its terms resolve; see its issues.</Text>
      )}
      <div className="qwb-grid" aria-label={`Cashflows of ${line}`}>
        <ExGrid<PeriodView> columns={periodColumns} source={source} onEdit={onEdit} cellState={periodCellState} cellMessage={cellMessage}
          density="compact" viewportWidth="stretch" viewportHeight={Math.min(64 + (flows.data?.periods.length ?? 3) * 28, 320)} locale="en-US" />
      </div>
      {flows.data !== undefined && flows.data.exchanges.length > 0 && <Exchanges flows={flows.data} />}
    </Stack>
  );
}

function Exchanges({ flows }: { readonly flows: CashflowsView }) {
  const amount = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 });
  return (
    <Table withTableBorder={false} fz="xs">
      <Table.Thead><Table.Tr><Table.Th>Leg</Table.Th><Table.Th>Exchange</Table.Th><Table.Th>Payment</Table.Th><Table.Th>Amount</Table.Th></Table.Tr></Table.Thead>
      <Table.Tbody>
        {flows.exchanges.map((e) => (
          <Table.Tr key={`${e.leg}-${e.kind}`}>
            <Table.Td>{e.leg}</Table.Td><Table.Td>{e.kind}</Table.Td><Table.Td>{e.paymentDate}</Table.Td>
            <Table.Td>{amount.format(e.amount)} {e.currency}</Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );
}

function ValuationDate({ value, disabled, onCommit }: { readonly value: string; readonly disabled: boolean; readonly onCommit: (date: string) => void }) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  const commit = () => { if (draft !== value && /^\d{4}-\d{2}-\d{2}$/.test(draft)) onCommit(draft); };
  return (
    <TextInput label="Valuation date" type="date" size="xs" w={150} value={draft} disabled={disabled}
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
    <Alert color="yellow" variant="light" title={`${conflicts.length} conflict(s), ${lost.length} lost cashflow edit(s)`} data-testid="held-edits">
      <Stack gap={4}>
        {conflicts.map((c) => (
          <Group key={`${c.entity}.${c.field}`} gap="xs" wrap="wrap">
            <Text size="xs" ff="monospace">{c.entity} {c.field}</Text>
            <Text size="xs">yours {value(c.value)}, schedule now {Object.entries(c.source ?? {}).map(([f, v]) => `${f} ${value(v)}`).join(', ')}</Text>
            <Button size="compact-xs" variant="default" onClick={() => resolve(c, 'keep')}>Keep mine</Button>
            <Button size="compact-xs" variant="default" onClick={() => resolve(c, 'takeSource')}>Take schedule</Button>
          </Group>
        ))}
        {lost.map((l) => (
          <Group key={`${l.entity}.${l.field}`} gap="xs" wrap="wrap">
            <Text size="xs" ff="monospace">{l.entity} {l.field}</Text>
            <Text size="xs">{value(l.value)}: the period is not in the new schedule{l.acknowledged ? ' (seen)' : ''}</Text>
            {!l.acknowledged && <Button size="compact-xs" variant="default" onClick={() => resolve(l, 'acknowledge')}>Seen</Button>}
            <Button size="compact-xs" variant="default" onClick={() => resolve(l, 'reset')}>Discard</Button>
          </Group>
        ))}
      </Stack>
    </Alert>
  );
}

