import { describe, expect, it } from 'vitest';
import { ApiError } from '../api';
import { EditQueue } from './editQueue';

const view = (revision: number) => ({ revision });

describe('EditQueue follows the revision on screen (ADR-0003, spine 6)', () => {
  it('chains the next edit on a revision someone else moved, once the page shows it', async () => {
    // The AI created a line (revision 5 to 6) and the page read the workspace again.
    const sent: number[] = [];
    const queue = new EditQueue<string, { revision: number }>(5, async (revision) => {
      if (revision !== 6) throw new ApiError(409, { code: 'RevisionMoved', message: 'moved', issues: [] });
      sent.push(revision);
      return view(revision + 1);
    }, () => {}, () => { throw new Error('refused'); });

    queue.shown(6);
    await queue.push('L1 notional');

    expect(sent).toEqual([6]);
    expect(queue.revision).toBe(7);
  });

  it('ignores a read while an edit of its own is waiting, and a read older than its own answer', async () => {
    let answer: ((v: { revision: number }) => void) | undefined;
    const sent: number[] = [];
    const queue = new EditQueue<string, { revision: number }>(5, (revision) => {
      sent.push(revision);
      return new Promise((resolve) => { answer = resolve; });
    }, () => {}, () => {});

    const first = queue.push('L1 notional');
    await Promise.resolve();
    queue.shown(9);
    expect(queue.revision).toBe(5);
    answer!(view(6));
    await first;

    queue.shown(5);
    expect(queue.revision).toBe(6);
    expect(sent).toEqual([5]);
  });
});
