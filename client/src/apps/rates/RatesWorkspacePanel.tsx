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
  PINNED_COLUMNS, lineCellMessage, lineCellState, lineColumns, hiddenEdits, lineHeaderGroups, lineRows, parserOf, periodCellState, periodColumns, periodParserOf,
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
  | { readonly kind: 'terms'; readonly terms: Readonly<Record<string, unknown>> }
  | { readonly kind: 'remove'; readonly line: string };

const describe = (error: unknown) => (error instanceof Error ? error.message : String(error));

/** Each column set made once: a new column is a new declaration, and every row paints again (ExGrid ADR-0003). */
const columnSets = new Map<string, ReturnType<typeof lineColumns>>();
const groupSets = new Map<string, ReturnType<typeof lineHeaderGroups>>();
function layoutOf(leg1: boolean, leg2: boolean, hides: { readonly leg1: boolean; readonly leg2: boolean }) {
  const key = `${leg1}/${leg2}`;
  let columns = columnSets.get(key);
  if (columns === undefined) columnSets.set(key, columns = lineColumns({ leg1, leg2 }));
  const groupKey = `${key}/${hides.leg1}/${hides.leg2}`;
  let groups = groupSets.get(groupKey);
  if (groups === undefined) groupSets.set(groupKey, groups = lineHeaderGroups({ leg1, leg2 }, hides));
  return { columns, groups };
}

export function RatesWorkspacePanel({ panelId, workspaceId, api }: { readonly panelId: string; readonly workspaceId: string; readonly api: RatesApi }) {
  const workspace = useWorkspaceContext();
  const queryClient = useQueryClient();
  const view = useQuery({ queryKey: ['rates', workspaceId], queryFn: () => api.view(workspaceId) });
  const market = useQuery({ queryKey: ['rates', 'market'], queryFn: () => api.market(), refetchInterval: 2000 });
  const conventions = useQuery({ queryKey: ['rates', 'conventions'], queryFn: () => api.conventions(), staleTime: Infinity });
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
        case 'terms': return (await api.createLine(workspaceId, revision, edit.terms)).workspace;
        case 'remove': return api.removeLine(workspaceId, revision, edit.line);
      }
    },
    (next) => { show(next); void queryClient.invalidateQueries({ queryKey: ['rates', workspaceId, 'cashflows'] }); },
    (edit, error) => {
      workspace.notify(edit.kind === 'terms'
        ? `The line was not created: ${describe(error)}`
        : `${edit.kind === 'line' || edit.kind === 'cashflow' ? `${edit.line} ${edit.field}` : edit.kind} was not changed: ${describe(error)}`);
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

  // Live (owner, 2026-10-27): the workspace's one subscription runs on the server; this page attaches to it
  // while it is open and only detaches when it goes. The server stops it once nobody has watched it for the
  // idle limit; Stop stops it for everyone. A stopped subscription is never resumed: Start makes a new one,
  // pricing the current revision from the next tick.
  const stream = useRef<FrameStream | undefined>(undefined);
  const detach = useCallback(() => {
    stream.current?.close();
    stream.current = undefined;
    setSubscription(undefined);
  }, []);
  const attach = useCallback((subscriptionId: string) => {
    if (stream.current !== undefined) return;
    setSubscription(subscriptionId);
    stream.current = api.frames(subscriptionId, setFrame, () => {
      detach();
      void queryClient.invalidateQueries({ queryKey: ['rates', workspaceId] });
    });
  }, [api, detach, queryClient, workspaceId]);
  const stopLive = useCallback(async (id: string | undefined) => {
    detach();
    if (id !== undefined) await api.stopLive(id);
    void queryClient.invalidateQueries({ queryKey: ['rates', workspaceId] });
  }, [api, detach, queryClient, workspaceId]);
  const startLive = useCallback(async () => {
    const { subscriptionId } = await api.startLive(workspaceId);
    attach(subscriptionId);
  }, [api, workspaceId, attach]);
  // On opening: attach to a running subscription, or show the last frame of a stopped one, marked as stopped.
  const liveStatus = view.data?.live;
  useEffect(() => {
    if (liveStatus === undefined) return;
    if (liveStatus.running) attach(liveStatus.subscriptionId);
    else if (liveStatus.lastFrame !== undefined) setFrame((shown) => shown ?? liveStatus.lastFrame);
  }, [liveStatus, attach]);
  useEffect(() => () => { stream.current?.close(); stream.current = undefined; }, []);

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
  const current = { price, startLive, stopLive: () => stopLive(subscription), live: subscription !== undefined, capture, queue, conventions: conventions.data ?? [] };
  const commands = useRef(current);
  commands.current = current;
  useEffect(() => {
    registerRatesCommands(panelId, {
      price: () => void commands.current.price(),
      startLive: () => {
        if (commands.current.live) workspace.notify('Live is running already.');
        else void commands.current.startLive();
      },
      stopLive: () => {
        if (!commands.current.live) workspace.notify('Live is not running.');
        else void commands.current.stopLive();
      },
      capture: () => void commands.current.capture(),
      addLine: (product) => void commands.current.queue.push({ kind: 'add', product }),
      createLine: (terms) => void commands.current.queue.push({ kind: 'terms', terms }),
      conventions: () => commands.current.conventions,
    });
    registerGridFocus(panelId, () => grid.current?.focus());
    return () => { registerRatesCommands(panelId, undefined); registerGridFocus(panelId, undefined); };
  }, [panelId, workspace]);

  if (view.isPending) return <Text c="dimmed">Loading workspace…</Text>;
  if (view.isError) return <Alert color="red" title="Workspace">{view.error.message}</Alert>;
  const ws = view.data;
  const layout = layoutOf(expanded.leg1, expanded.leg2, hiddenEdits(ws.lines));
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
          {subscription !== undefined ? 'Stop live' : ws.live !== undefined && !ws.live.running ? 'Resume live' : 'Start live'}
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
        {subscription === undefined && ws.live !== undefined && !ws.live.running && (
          <Badge variant="light" color="gray" data-testid="live-stopped">
            {ws.live.stoppedBy === 'idle' ? `live stopped: nobody watched it for ${idleText(ws.live.idleLimit)}`
              : ws.live.stoppedFrom === 'ai' ? 'live stopped by the AI' : 'live stopped'}
            {ws.live.stoppedAt === undefined ? '' : ` at ${new Date(ws.live.stoppedAt).toLocaleTimeString()}`}
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

/** A .NET TimeSpan (`hh:mm:ss`) as minutes or seconds. */
function idleText(span: string): string {
  const [h, m, s] = span.split(':').map(Number) as [number, number, number];
  const seconds = h * 3600 + m * 60 + s;
  return seconds % 60 === 0 ? `${seconds / 60} min` : `${seconds} s`;
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

