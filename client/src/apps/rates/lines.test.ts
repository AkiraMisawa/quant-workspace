import type { LineView, LiveFrame } from './contracts';
import { hiddenEdits, lineColumns, lineHeaderGroups, lineResult, lineRows, parserOf } from './lines';

const line = (name: string): LineView => ({
  line: name, fields: {}, userFields: [], priceable: true, parLeg: 1, spot: null, profiles: [], issues: [], held: [], defaulted: {},
});

const frame = (inputRevision: number): LiveFrame => ({
  subscriptionId: 's', subscriptionRevision: 1, inputRevision,
  market: { sequence: 3, snapshotId: 'synthetic:t:1:3', asOf: '2026-10-27T00:00:03Z', origin: 'synthetic' },
  completedAt: '2026-10-27T00:00:03Z', provider: 'fake-rates/1', calculationMode: 'simulated',
  rows: [{ rowKey: 'L2', measures: { pv: { value: 12.5, unit: 'JPY' }, parRate: { value: 0.61, unit: '%/leg1' } } }],
  notPriced: [{ line: 'L3', reasons: ['The end is empty.'] }],
});

describe('what a line cell accepts (C40, C43)', () => {
  it('reads an end as a date or a tenor, and refuses anything else', () => {
    expect(parserOf('end')!('5Y')).toEqual({ ok: true, value: '5y' });
    expect(parserOf('end')!('2031-10-29')).toEqual({ ok: true, value: '2031-10-29' });
    expect(parserOf('end')!('five years').ok).toBe(false);
    expect(parserOf('end')!('').ok).toBe(false);
    expect(parserOf('start')!('')).toEqual({ ok: true, value: null });
  });

  it('keeps an empty quote empty, so the leg is solved at par, never zero', () => {
    expect(parserOf('leg1.quote')!('')).toEqual({ ok: true, value: null });
    expect(parserOf('leg1.quoteKind')!('fixed')).toEqual({ ok: true, value: 'FixedRatePercent' });
    expect(parserOf('leg1.quoteKind')!('bp').ok).toBe(false);
  });

  it('takes only the payment frequencies offered, and the second level by its short names', () => {
    expect(parserOf('leg2.paymentFrequency')!('3M')).toEqual({ ok: true, value: '3m' });
    expect(parserOf('leg2.paymentFrequency')!('4m').ok).toBe(false);
    expect(parserOf('leg2.dayCount')!('ACT/365F')).toEqual({ ok: true, value: 'Act365Fixed' });
    expect(parserOf('leg2.adjustment')!('mf')).toEqual({ ok: true, value: 'ModifiedFollowing' });
    expect(parserOf('leg2.lookback')!('2.5').ok).toBe(false);
    expect(parserOf('line')).toBeUndefined();
  });
});

describe('the line grid', () => {
  it('groups exactly the columns it shows, collapsed or expanded', () => {
    for (const expanded of [{ leg1: false, leg2: false }, { leg1: true, leg2: false }, { leg1: true, leg2: true }]) {
      const names = new Set(lineColumns(expanded).map((c) => c.name));
      for (const group of lineHeaderGroups(expanded)) for (const c of group.columns) expect(names.has(c)).toBe(true);
    }
    expect(lineColumns({ leg1: true, leg2: false }).length - lineColumns({ leg1: false, leg2: false }).length).toBe(9);
  });

  it('marks a collapsed leg whose hidden columns hold a value the user set, and only while it is collapsed (ExGrid ADR-0400)', () => {
    const edited = { ...line('L2'), userFields: ['leg2.lookback'] };
    const hides = hiddenEdits([line('L1'), edited]);
    const leg2 = (open: boolean) => lineHeaderGroups({ leg1: false, leg2: open }, hides).find((g) => g.id === 'leg2')!;

    expect(hides).toEqual({ leg1: 0, leg2: 1 });
    expect(leg2(false)).toMatchObject({ label: 'Leg 2', expanded: false, hiddenMark: { state: 'modified' } });
    expect(leg2(true).expanded).toBe(true);
    expect(leg2(true).hiddenMark).toBeUndefined();
  });

  it('puts the inputs first and the result after them (owner, 2026-11-04)', () => {
    const names = lineColumns({ leg1: false, leg2: false }).map((c) => c.name);
    expect(names.slice(0, 3)).toEqual(['line', 'Actions', 'product']);
    expect(names.indexOf('status')).toBeGreaterThan(names.indexOf('risk'));
    expect(names.at(-1)).toBe('message');
  });

  it('marks figures of an older revision stale, and lists a line left out as not priced', () => {
    const rows = lineRows([line('L2'), line('L3')], 7, frame(6), new Map());

    expect(rows[0]!.figures).toMatchObject({ status: 'priced', pv: 12.5, par: 0.61, parMeasure: 'parRate' });
    expect(rows[0]!.figuresStale).toBe(true);
    expect(rows[1]!.figures?.status).toBe('notPriced');
    expect(rows[1]!.figures?.pv).toBeUndefined();
  });

  it('keeps a row object whose figures did not move, so the grid does not paint it again', () => {
    const views = [line('L2')];
    const first = lineRows(views, 6, frame(6), new Map());
    const again = lineRows(views, 6, frame(6), new Map(first.map((r) => [r.line, r])));

    expect(again[0]).toBe(first[0]);
  });
});

describe('a notional in millions (owner, 2026-11-04)', () => {
  const read = parserOf('leg1.notional')!;

  it('reads a bare number as millions, and a scale as written', () => {
    expect(read('1000')).toEqual({ ok: true, value: 1e9 });
    expect(read('2.5')).toEqual({ ok: true, value: 2.5e6 });
    expect(read('3bn')).toEqual({ ok: true, value: 3e9 });
    expect(read('500mm')).toEqual({ ok: true, value: 5e8 });
    // `m` is months (ADR-0008 §2), so it is not a scale.
    expect(read('500m').ok).toBe(false);
    expect(read('')).toEqual({ ok: true, value: null });
  });

  it('refuses a full amount typed out of habit rather than make it a million times larger (spine 1)', () => {
    const typed = read('1,000,000,000');
    expect(typed.ok).toBe(false);
    expect(typed.ok ? '' : typed.message).toContain('millions');
  });

  it('shows a notional in millions under a header that says so', () => {
    const column = lineColumns({ leg1: false, leg2: false }).find((c) => c.name === 'leg1.notional')!;
    expect(column.header).toBe('Notional (mm)');
    expect(column.format!(1e9)).toBe('1,000');
    expect(column.format!(2.5e6)).toBe('2.5');
  });
});

describe("the strip with the focused line's result (owner, 2026-11-04)", () => {
  it('shows the figures with their unit and measure, marked stale while a newer revision waits', () => {
    const [priced, left] = lineRows([line('L2'), line('L3')], 7, frame(6), new Map());

    expect(lineResult(priced!)).toEqual({ line: 'L2', status: 'priced', pv: '13 JPY', par: '0.61000%', parLabel: 'Par rate', stale: true });
    expect(lineResult(left!)).toMatchObject({ line: 'L3', status: 'notPriced', stale: true });
    expect(lineResult(left!).pv).toBeUndefined();
  });

  it('says a line has no figures yet rather than show a zero', () => {
    const [row] = lineRows([{ ...line('L4'), issues: ['The end is empty.'] }], 7, undefined, new Map());
    expect(lineResult(row!)).toEqual({ line: 'L4', status: 'none', stale: false, issues: 'The end is empty.' });
  });
});
