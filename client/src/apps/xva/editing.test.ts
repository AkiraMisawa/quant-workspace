import { ApiError } from '../../platform/api';
import type { XvaWorkspaceView } from './contracts';
import { EditQueue, parseNumber, parseRecovery, tradeFields, type PendingEdit } from './editing';

const view = (revision: number) => ({ revision }) as unknown as XvaWorkspaceView;

describe('what a cell accepts', () => {
  it('reads a number only one way', () => {
    expect(parseNumber('1,000,000')).toBe(1_000_000);
    expect(parseNumber('-0.0125')).toBe(-0.0125);
    expect(parseNumber('1,00')).toBeUndefined();
    expect(parseNumber('1.000,5')).toBeUndefined();
    expect(parseNumber('1e6')).toBeUndefined();
  });

  it('keeps an empty recovery and an empty fixed rate empty, never zero', () => {
    expect(parseRecovery('')).toEqual({ ok: true, value: null });
    expect(tradeFields.fixedRate!('')).toEqual({ ok: true, value: null });
    expect(parseRecovery('1').ok).toBe(false);
  });

  it('refuses a zero notional and a schedule it does not know', () => {
    expect(tradeFields.notional!('0').ok).toBe(false);
    expect(tradeFields.schedule!('1Y').ok).toBe(false);
    expect(tradeFields.before!('yes').ok).toBe(false);
    expect(tradeFields.after!('false')).toEqual({ ok: true, value: false });
  });
});

describe('EditQueue (ADR-0003, spine 6)', () => {
  it("chains this session's edits on the revision each produced", async () => {
    const sent: number[] = [];
    const queue = new EditQueue<PendingEdit, XvaWorkspaceView>(2, async (revision) => { sent.push(revision); return view(revision + 1); }, () => {}, () => {});

    await Promise.all([queue.push({ entity: 'trade:A', field: 'notional', value: 1 }), queue.push({ entity: 'trade:B', field: 'notional', value: 2 })]);

    expect(sent).toEqual([2, 3]);
    expect(queue.revision).toBe(4);
  });

  it('drops what was queued behind a revision moved elsewhere, until the workspace is read again', async () => {
    const refused: PendingEdit[] = [];
    const queue = new EditQueue<PendingEdit, XvaWorkspaceView>(5, async (revision) => {
      if (revision === 5) throw new ApiError(409, { code: 'RevisionMoved', message: 'moved', issues: [] });
      return view(revision + 1);
    }, () => {}, (edit) => refused.push(edit));

    await queue.push({ entity: 'trade:A', field: 'csa', value: 'CSA-B' });
    await queue.push({ entity: 'trade:B', field: 'csa', value: 'CSA-B' });
    queue.rebase(7);
    await queue.push({ entity: 'trade:C', field: 'csa', value: 'CSA-B' });

    expect(refused.map((e) => e.entity)).toEqual(['trade:A', 'trade:B']);
    expect(queue.revision).toBe(8);
  });
});
