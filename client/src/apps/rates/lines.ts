import type { CellState, GridColumn, GridHeaderGroup } from '@ex-grid/core';
import { actionColumn } from '@ex-grid/core';
import { parseBoolean, parseNumber, type Parsed } from '../xva/editing';
import type { LineView, LiveFrame, PeriodView } from './contracts';

// The line grid's model (C40, C43): a line's flat fields as columns, the second level of each leg shown on
// request, and the latest figures beside them. Only the form of what was typed is checked here; whether the
// terms make a line is the server's, and shows on the row.

/** The figures a row shows, and what they were taken against. */
export interface LineFigures {
  readonly status: 'priced' | 'failed' | 'notPriced';
  readonly pv?: number;
  readonly pvCurrency?: string;
  /** The solved leg's quote: a par rate (fixed leg) or a fair spread (floating leg), in percent. */
  readonly par?: number;
  readonly parMeasure?: string;
  readonly message?: string;
  /** The input revision and the market the figures were calculated on. */
  readonly inputRevision: number;
  readonly snapshotId: string;
  readonly mode: string;
}

/** One row of the line grid: the line, and its figures if a frame has any. */
export interface LineRow {
  readonly line: string;
  readonly view: LineView;
  readonly figures: LineFigures | undefined;
  /** Whether the figures were calculated on an older revision than the row shows. */
  readonly figuresStale: boolean;
}

const fail = (message: string): Parsed => ({ ok: false, message });
const empty = (text: string) => text.trim() === '';

const TENOR = /^[1-9]\d{0,2}[dwmy]$/i;
const DATE = /^\d{4}-\d{2}-\d{2}$/;

function dateOrTenor(text: string, allowEmpty: boolean): Parsed {
  if (empty(text)) return allowEmpty ? { ok: true, value: null } : fail('Type a date (yyyy-mm-dd) or a tenor such as 5y.');
  const t = text.trim();
  if (DATE.test(t) || TENOR.test(t)) return { ok: true, value: t.toLowerCase() };
  return fail('Type a date (yyyy-mm-dd) or a tenor such as 5y.');
}

function choice(options: Readonly<Record<string, string>>, help: string) {
  return (text: string): Parsed => {
    if (empty(text)) return { ok: true, value: null };
    const value = options[text.trim().toLowerCase()];
    return value === undefined ? fail(help) : { ok: true, value };
  };
}

function number(text: string): Parsed {
  if (empty(text)) return { ok: true, value: null };
  const n = parseNumber(text);
  return n === undefined ? fail('Type a number, such as 1,000,000 or 0.25, or leave it empty.') : { ok: true, value: n };
}

function whole(text: string): Parsed {
  if (empty(text)) return { ok: true, value: null };
  const n = parseNumber(text);
  return n === undefined || !Number.isInteger(n) || n < 0 ? fail('Type a whole number of days, or leave it empty.') : { ok: true, value: n };
}

function flag(text: string): Parsed {
  if (empty(text)) return { ok: true, value: null };
  const b = parseBoolean(text);
  return b === undefined ? fail('Type TRUE or FALSE.') : { ok: true, value: b };
}

function frequency(allowEmpty: boolean) {
  return (text: string): Parsed => {
    if (empty(text)) return allowEmpty ? { ok: true, value: null } : fail('Type 1m, 2m, 3m, 6m or 12m.');
    const t = text.trim().toLowerCase();
    return ['1m', '2m', '3m', '6m', '12m'].includes(t) ? { ok: true, value: t } : fail('Type 1m, 2m, 3m, 6m or 12m.');
  };
}

const code = (what: string) => (text: string): Parsed => {
  if (empty(text)) return { ok: true, value: null };
  const t = text.trim().toUpperCase();
  return /^[A-Z0-9_-]+$/.test(t) ? { ok: true, value: t } : fail(`Type ${what} in letters and digits.`);
};

/** The quotation a leg's Rate/Strike is given in (C40). The wire names are the server's enum. */
export const quoteKinds: Readonly<Record<string, string>> = {
  fixed: 'FixedRatePercent', fixedratepercent: 'FixedRatePercent',
  spread: 'FloatSpreadPercent', floatspreadpercent: 'FloatSpreadPercent',
  strike: 'StrikePercent', strikepercent: 'StrikePercent',
  fxpts: 'FxForwardPoints', fxforwardpoints: 'FxForwardPoints',
  atm: 'AtmOffsetBp', atmoffsetbp: 'AtmOffsetBp',
};

const quoteLabel: Readonly<Record<string, string>> = {
  FixedRatePercent: 'Fixed %', FloatSpreadPercent: 'Spread %', StrikePercent: 'Strike %', FxForwardPoints: 'FX pts', AtmOffsetBp: 'ATM ± bp',
};

/** The basic terms of a leg (C40), in display order. */
export const legBasic = ['currency', 'index', 'notional', 'quoteKind', 'quote', 'paymentFrequency', 'resetFrequency', 'floating'] as const;
/** The second level of a leg's terms (C43), collapsed until asked for. */
export const legDetail = ['dayCount', 'adjustment', 'dailyAverage', 'observationShift', 'lookback', 'paymentDelay', 'inArrears', 'gearing', 'currentReset'] as const;

const legParsers: Readonly<Record<string, (text: string) => Parsed>> = {
  currency: code('a currency'),
  index: code('an index'),
  notional: number,
  quoteKind: choice(quoteKinds, 'Type fixed, spread, strike, fxpts or atm, or leave it empty for par.'),
  quote: number,
  paymentFrequency: frequency(false),
  resetFrequency: frequency(true),
  floating: flag,
  dayCount: choice({ 'act/360': 'Act360', act360: 'Act360', 'act/365f': 'Act365Fixed', act365f: 'Act365Fixed', act365fixed: 'Act365Fixed' }, 'Type ACT/360 or ACT/365F, or leave it empty for the profile\'s.'),
  adjustment: choice({ mf: 'ModifiedFollowing', modifiedfollowing: 'ModifiedFollowing', f: 'Following', following: 'Following', p: 'Preceding', preceding: 'Preceding', u: 'Unadjusted', unadjusted: 'Unadjusted' }, 'Type MF, F, P or U, or leave it empty for the profile\'s.'),
  dailyAverage: flag,
  observationShift: whole,
  lookback: whole,
  paymentDelay: whole,
  inArrears: flag,
  gearing: number,
  currentReset: number,
};

/** How each editable line field reads what was typed into its cell. */
export function parserOf(field: string): ((text: string) => Parsed) | undefined {
  switch (field) {
    case 'start': return (t) => dateOrTenor(t, true);
    case 'end': return (t) => dateOrTenor(t, false);
    case 'direction': return choice({ p: 'pay', pay: 'pay', r: 'receive', receive: 'receive', rec: 'receive' }, 'Type P (pay) or R (receive).');
    case 'collateral': return (t) => (empty(t) ? { ok: true, value: null } : { ok: true, value: t.trim() });
    case 'mandatoryBreak': return (t) => (empty(t) ? { ok: true, value: null } : DATE.test(t.trim()) ? { ok: true, value: t.trim() } : fail('Type a date (yyyy-mm-dd), or leave it empty.'));
    case 'risk': return (t) => { const b = parseBoolean(t); return b === undefined ? fail('Type TRUE or FALSE.') : { ok: true, value: b }; };
    default: {
      const m = /^leg[12]\.(.+)$/.exec(field);
      return m === null ? undefined : legParsers[m[1] as string];
    }
  }
}

/** How each editable cashflow field reads what was typed. */
export function periodParserOf(field: string): ((text: string) => Parsed) | undefined {
  switch (field) {
    case 'accrualStart': case 'accrualEnd': case 'paymentDate':
      return (t) => (DATE.test(t.trim()) ? { ok: true, value: t.trim() } : fail('Type a date (yyyy-mm-dd).'));
    case 'notional': return (t) => { const n = parseNumber(t); return n === undefined ? fail('Type a number.') : { ok: true, value: n }; };
    case 'rate': return number;
    default: return undefined;
  }
}

const amount = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 });
const pct = new Intl.NumberFormat('en-US', { minimumFractionDigits: 5, maximumFractionDigits: 5 });
const plain = (v: unknown) => (typeof v === 'boolean' ? (v ? 'TRUE' : 'FALSE') : String(v));

const field = (name: string, header: string, type: GridColumn<LineRow>['type'], width: number, format?: (v: unknown) => string): GridColumn<LineRow> => ({
  name, header, type, width, editable: true, value: (row) => row.view.fields[name] ?? undefined, format: format ?? plain,
});

const legHeaders: Readonly<Record<string, readonly [string, GridColumn<LineRow>['type'], number, ((v: unknown) => string)?]>> = {
  currency: ['Ccy', 'text', 56],
  index: ['Index', 'text', 72],
  notional: ['Notional', 'number', 150, (v) => amount.format(v as number)],
  quoteKind: ['Quote type', 'text', 84, (v) => quoteLabel[v as string] ?? String(v)],
  quote: ['Rate/Strike', 'number', 92],
  paymentFrequency: ['Pay freq', 'text', 70],
  resetFrequency: ['Reset freq', 'text', 76],
  floating: ['Float', 'boolean', 56],
  dayCount: ['Day count', 'text', 90],
  adjustment: ['BDC', 'text', 120],
  dailyAverage: ['Daily avg', 'boolean', 72],
  observationShift: ['Obs shift', 'number', 70],
  lookback: ['Lookback', 'number', 72],
  paymentDelay: ['Pay delay', 'number', 72],
  inArrears: ['In arrears', 'boolean', 78],
  gearing: ['Gearing', 'number', 70],
  currentReset: ['Cur. reset', 'number', 78],
};

function legColumns(leg: 1 | 2, expanded: boolean): GridColumn<LineRow>[] {
  return [...legBasic, ...(expanded ? legDetail : [])].map((f) => {
    const [header, type, width, format] = legHeaders[f] as readonly [string, GridColumn<LineRow>['type'], number, ((v: unknown) => string)?];
    return field(`leg${leg}.${f}`, header, type, width, format);
  });
}

const figure = (name: string, header: string, type: GridColumn<LineRow>['type'], width: number, read: (f: LineFigures) => unknown, format: (v: unknown) => string): GridColumn<LineRow> => ({
  name, header, type, width, value: (row) => (row.figures === undefined ? undefined : read(row.figures) ?? undefined), format,
});

export const ACTIONS = 'Actions';

/** The line grid's columns for which legs are expanded. Made once per combination (ExGrid ADR-0003). */
export function lineColumns(expanded: { readonly leg1: boolean; readonly leg2: boolean }): readonly GridColumn<LineRow>[] {
  return [
    // Pinned: which line, and what to do with it, stay in view however far the terms scroll.
    { name: 'line', header: 'Line', type: 'text', width: 56, value: (row) => row.line, format: plain },
    actionColumn<LineRow>(ACTIONS, [{ name: 'cashflows', label: 'Cashflows' }, { name: 'remove', label: 'Remove' }], { width: 164 }),
    figure('status', 'Status', 'text', 82, (f) => f.status, plain),
    figure('pv', 'PV', 'number', 140, (f) => f.pv, (v) => amount.format(v as number)),
    figure('pvCurrency', 'Ccy', 'text', 52, (f) => f.pvCurrency, plain),
    figure('par', 'Par / fair %', 'number', 100, (f) => f.par, (v) => pct.format(v as number)),
    field('product', 'Product', 'text', 64, (v) => String(v).toUpperCase()),
    field('start', 'Start', 'text', 96),
    field('end', 'End', 'text', 96),
    field('direction', 'P/R', 'text', 52, (v) => (v === 'pay' ? 'Pay' : v === 'receive' ? 'Rec' : String(v))),
    ...legColumns(1, expanded.leg1),
    ...legColumns(2, expanded.leg2),
    field('collateral', 'Collateral', 'text', 90),
    field('mandatoryBreak', 'Break', 'text', 96),
    field('risk', 'Risk', 'boolean', 56),
    { name: 'message', header: 'Issues', type: 'text', width: 320, value: (row) => message(row), format: plain },
  ];
}

/** How many columns from the left stay in place: the line and its actions. */
export const PINNED_COLUMNS = 2;

/**
 * Whether a collapsed leg hides a value the user set, on any line (owner, 2026-10-27): collapsing only hides it,
 * and it is still priced, so the group says so. Stands in for ExGrid's own mark until its Header Group collapses.
 */
export function hiddenEdits(lines: readonly LineView[]): { readonly leg1: boolean; readonly leg2: boolean } {
  const hides = (n: 1 | 2) => lines.some((l) => legDetail.some((f) => l.userFields.includes(`leg${n}.${f}`)));
  return { leg1: hides(1), leg2: hides(2) };
}

/** The Header Groups over the columns (ExGrid ADR-0032): one tier naming the result, the trade, each leg and its terms. */
export function lineHeaderGroups(expanded: { readonly leg1: boolean; readonly leg2: boolean },
  hidden: { readonly leg1: boolean; readonly leg2: boolean } = { leg1: false, leg2: false }): readonly GridHeaderGroup[] {
  const leg = (n: 1 | 2, open: boolean) => [...legBasic, ...(open ? legDetail : [])].map((f) => `leg${n}.${f}`);
  const caption = (n: 1 | 2, open: boolean, hides: boolean) => `Leg ${n} ${open ? '▾' : '▸'}${!open && hides ? ' ●' : ''}`;
  return [
    { label: 'Result', columns: ['status', 'pv', 'pvCurrency', 'par'] },
    { label: 'Trade', columns: ['product', 'start', 'end', 'direction'] },
    { label: caption(1, expanded.leg1, hidden.leg1), columns: leg(1, expanded.leg1) },
    { label: caption(2, expanded.leg2, hidden.leg2), columns: leg(2, expanded.leg2) },
    { label: 'Terms', columns: ['collateral', 'mandatoryBreak', 'risk'] },
  ];
}

function message(row: LineRow): string | undefined {
  const parts = [...row.view.issues, ...row.view.held];
  if (row.figures?.message !== undefined) parts.push(row.figures.message);
  return parts.length === 0 ? undefined : parts.join(' · ');
}

const RESULT = new Set(['status', 'pv', 'pvCurrency', 'par']);

/** A cell's state: the user's edits, held edits, a line that cannot be priced, and figures of an older revision. */
export function lineCellState(row: LineRow, column: GridColumn<LineRow>): CellState {
  if (RESULT.has(column.name)) {
    if (row.figures === undefined) return 'normal';
    if (row.figuresStale) return 'stale';
    return row.figures.status === 'priced' ? 'normal' : 'error';
  }
  if (column.name === 'message') return message(row) === undefined ? 'normal' : 'error';
  if (row.view.held.some((h) => h.startsWith(`${row.line} ${column.name}:`))) return 'stale';
  if (row.view.userFields.includes(column.name)) return 'modified';
  return 'normal';
}

export function lineCellMessage(row: LineRow, column: GridColumn<LineRow>): string | undefined {
  if (RESULT.has(column.name) && row.figures !== undefined) {
    const f = row.figures;
    const when = `revision ${f.inputRevision}, market ${f.snapshotId}, ${f.mode}`;
    return row.figuresStale ? `Calculated on an older revision (${when}); the line changed since.` : `Calculated on ${when}.`;
  }
  if (column.name === 'message') return message(row);
  if (column.name.startsWith('leg') && row.view.profiles.length > 0 && (column.name.endsWith('.index') || column.name.endsWith('.currency')))
    return `Conventions: ${row.view.profiles.join(', ')}`;
  if (row.view.userFields.includes(column.name)) return 'Set by you.';
  return undefined;
}

/** The figures of each line in a live frame, keyed by line. */
export function figuresOf(frame: LiveFrame): ReadonlyMap<string, LineFigures> {
  const out = new Map<string, LineFigures>();
  const base = { inputRevision: frame.inputRevision, snapshotId: frame.market.snapshotId, mode: frame.calculationMode };
  for (const row of frame.rows) {
    if (row.failure !== undefined && row.failure !== null) { out.set(row.rowKey, { ...base, status: 'failed', message: row.failure }); continue; }
    const pv = row.measures.pv;
    const parName = row.measures.parRate !== undefined ? 'parRate' : row.measures.fairSpread !== undefined ? 'fairSpread' : undefined;
    out.set(row.rowKey, {
      ...base, status: 'priced',
      ...(pv === undefined ? {} : { pv: pv.value, pvCurrency: pv.unit }),
      ...(parName === undefined ? {} : { par: row.measures[parName]!.value, parMeasure: parName }),
    });
  }
  for (const left of frame.notPriced) out.set(left.line, { ...base, status: 'notPriced' });
  return out;
}

/**
 * The rows: one per line, with the newest frame's figures. A line whose figures were calculated on an older
 * revision keeps them, marked stale, until a frame of its revision arrives; never shown as current.
 */
export function lineRows(lines: readonly LineView[], revision: number, frame: LiveFrame | undefined, previous: ReadonlyMap<string, LineRow>): LineRow[] {
  const figures = frame === undefined ? undefined : figuresOf(frame);
  return lines.map((view) => {
    const f = figures?.get(view.line);
    const stale = frame !== undefined && frame.inputRevision < revision;
    const before = previous.get(view.line);
    // An unchanged row keeps its object, so the grid paints it again only when something it shows moved.
    if (before !== undefined && before.view === view && before.figuresStale === stale
      && (before.figures === f || (before.figures !== undefined && f !== undefined && sameFigures(before.figures, f)))) return before;
    return { line: view.line, view, figures: f, figuresStale: stale };
  });
}

function sameFigures(a: LineFigures, b: LineFigures): boolean {
  return a.status === b.status && a.pv === b.pv && a.par === b.par && a.message === b.message && a.inputRevision === b.inputRevision && a.snapshotId === b.snapshotId;
}

/** The cashflow grid: one row per period, the user's edits marked. */
export const periodColumns: readonly GridColumn<PeriodView>[] = [
  { name: 'leg', header: 'Leg', type: 'number', width: 44, value: (p) => p.leg, format: plain },
  { name: 'accrualStart', header: 'Accrual start', type: 'text', width: 104, editable: true, value: (p) => p.accrualStart, format: plain },
  { name: 'accrualEnd', header: 'Accrual end', type: 'text', width: 104, editable: true, value: (p) => p.accrualEnd, format: plain },
  { name: 'paymentDate', header: 'Payment', type: 'text', width: 104, editable: true, value: (p) => p.paymentDate, format: plain },
  { name: 'notional', header: 'Notional', type: 'number', width: 140, editable: true, value: (p) => p.notional, format: (v) => amount.format(v as number) },
  { name: 'rate', header: 'Rate/Spread %', type: 'number', width: 110, editable: true, value: (p) => p.rate ?? undefined, format: plain },
  { name: 'yearFraction', header: 'Year fraction', type: 'number', width: 100, value: (p) => p.yearFraction, format: (v) => (v as number).toFixed(6) },
];

export function periodCellState(p: PeriodView, column: GridColumn<PeriodView>): CellState {
  return p.userFields.includes(column.name) ? 'modified' : 'normal';
}
