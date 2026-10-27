import { buildCommands } from './CommandPalette';
import { catalog, view, workflow } from '../test/catalog';
import type { RunRecord } from '../platform/contracts';
import type { WorkspaceState } from '../platform/workspace/workspaceStore';

const state = (overrides: Partial<WorkspaceState> = {}) => ({
  panels: [],
  activePanelId: undefined,
  context: { requested: {}, prepared: { region: 'North', asOf: '2026-10-26' }, issues: [] },
  openRunDialog: vi.fn(),
  setPanelView: vi.fn(),
  closePanel: vi.fn(),
  cycle: vi.fn(),
  requestContext: vi.fn(),
  ...overrides,
}) as Parameters<typeof buildCommands>[0]['state'];

const build = (cat: ReturnType<typeof catalog>, s = state(), activeRun?: RunRecord) =>
  buildCommands({ catalog: cat, state: s, activeRun, toggleTheme: vi.fn(), notify: vi.fn() });

describe('palette commands', () => {
  it('CO01: a workflow added to the catalog gets a run command with no client change', () => {
    const titles = build(catalog([workflow('w.a', 'Station summary', ['s']), workflow('w.new', 'Rates curve check', ['c'])], [])).map((c) => c.title);
    expect(titles).toContain('Run Rates curve check');
  });

  it('makes run commands unavailable while the context is not valid', () => {
    const s = state({ context: { requested: {}, prepared: undefined, issues: [] } });
    const run = build(catalog([workflow('w.a', 'A', ['s'])], []), s).find((c) => c.title === 'Run A')!;
    expect(run.unavailable).toBe('The context is not valid.');
  });

  it('offers the views of the active Run only once it has finished', () => {
    const w = workflow('w.a', 'A', ['summary']);
    const cat = catalog([w], [view('v.1', 'Summary', ['summary']), view('v.2', 'Other', ['other'])]);
    const s = state({ panels: [{ id: 'p', kind: 'run', title: 'A', runId: 'r', viewKey: undefined }], activePanelId: 'p' });
    const record = (status: RunRecord['status']) => ({ runId: 'r', workflow: w, status }) as RunRecord;
    expect(build(cat, s, record('running')).filter((c) => c.group === 'Views')).toEqual([]);
    expect(build(cat, s, record('partiallySucceeded')).filter((c) => c.group === 'Views').map((c) => c.title)).toEqual(['Show as Summary']);
  });
});
