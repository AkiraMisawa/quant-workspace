import { createWorkspaceStore } from './workspaceStore';
import { contextContract } from '../../test/catalog';

const valid = { region: 'North', asOf: '2026-10-26' };

describe('workspace store', () => {
  it('keeps the last valid context prepared while the requested one is invalid', () => {
    const store = createWorkspaceStore(valid, contextContract);
    store.getState().requestContext(contextContract, { asOf: '2026-13-01' });
    expect(store.getState().context.prepared).toEqual(valid);
    expect(store.getState().context.issues.map((i) => i.field)).toEqual(['asOf']);
    store.getState().requestContext(contextContract, { asOf: '2026-10-27', region: 'South' });
    expect(store.getState().context.prepared).toEqual({ region: 'South', asOf: '2026-10-27' });
  });

  it('activates the neighbour when the active panel closes, and cycles around the ends', () => {
    const store = createWorkspaceStore(valid, contextContract);
    const { openPanel } = store.getState();
    const a = openPanel({ kind: 'run', title: 'A', runId: 'r1', viewKey: undefined });
    const b = openPanel({ kind: 'run', title: 'B', runId: 'r2', viewKey: undefined });
    const c = openPanel({ kind: 'run', title: 'C', runId: 'r3', viewKey: undefined });
    store.getState().cycle(1);
    expect(store.getState().activePanelId).toBe(a);
    store.getState().activate(b);
    store.getState().closePanel(b);
    expect(store.getState().activePanelId).toBe(c);
    store.getState().closePanel(c);
    expect(store.getState().activePanelId).toBe(a);
  });
});
