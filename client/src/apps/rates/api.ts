import { call } from '../../platform/api';
import type { RunRecord } from '../../platform/contracts';
import type { CarrySummary, CashflowsView, LiveFrame, MarketView, RatesWorkspaceView, Resolution } from './contracts';

const base = (workspaceId: string) => `/api/rates/workspaces/${encodeURIComponent(workspaceId)}`;
const post = <T>(path: string, body: unknown) => call<T>(path, { method: 'POST', body: JSON.stringify(body) });

/** A running live subscription's frames. `close` stops hearing; it does not stop the subscription. */
export interface FrameStream { close(): void }

/** The Rates line pricer's endpoints. Every command names the revision it was taken against. */
export interface RatesApi {
  market(): Promise<MarketView>;
  create(valuationDate: string): Promise<RatesWorkspaceView>;
  view(workspaceId: string): Promise<RatesWorkspaceView>;
  addLine(workspaceId: string, revision: number, product: 'irs' | 'ccs'): Promise<{ readonly line: string; readonly workspace: RatesWorkspaceView }>;
  removeLine(workspaceId: string, revision: number, line: string): Promise<RatesWorkspaceView>;
  editLine(workspaceId: string, revision: number, line: string, field: string, value: unknown): Promise<RatesWorkspaceView>;
  cashflows(workspaceId: string, line: string): Promise<CashflowsView>;
  editCashflow(workspaceId: string, revision: number, line: string, period: string, field: string, value: unknown): Promise<RatesWorkspaceView>;
  resolve(workspaceId: string, revision: number, scope: string, entity: string, field: string, resolution: Resolution): Promise<RatesWorkspaceView>;
  changeValuationDate(workspaceId: string, revision: number, valuationDate: string): Promise<{ readonly workspace: RatesWorkspaceView; readonly carried: CarrySummary }>;
  price(workspaceId: string, revision: number, idempotencyKey: string, marketSnapshotId: string): Promise<RunRecord>;
  startLive(workspaceId: string): Promise<{ readonly subscriptionId: string }>;
  stopLive(subscriptionId: string): Promise<void>;
  frames(subscriptionId: string, onFrame: (frame: LiveFrame) => void, onBroken: () => void): FrameStream;
  capture(subscriptionId: string, frame: LiveFrame, idempotencyKey: string): Promise<RunRecord>;
}

export const httpRatesApi: RatesApi = {
  market: () => call('/api/rates/market'),
  create: (valuationDate) => post('/api/rates/workspaces', { valuationDate }),
  view: (workspaceId) => call(base(workspaceId)),
  addLine: (workspaceId, revision, product) => post(`${base(workspaceId)}/lines`, { revision, product }),
  removeLine: (workspaceId, revision, line) => post(`${base(workspaceId)}/lines/${encodeURIComponent(line)}/remove`, { revision }),
  editLine: (workspaceId, revision, line, field, value) => post(`${base(workspaceId)}/lines/${encodeURIComponent(line)}/edits`, { revision, field, value }),
  cashflows: (workspaceId, line) => call(`${base(workspaceId)}/lines/${encodeURIComponent(line)}/cashflows`),
  editCashflow: (workspaceId, revision, line, period, field, value) =>
    post(`${base(workspaceId)}/lines/${encodeURIComponent(line)}/cashflows/edits`, { revision, period, field, value }),
  resolve: (workspaceId, revision, scope, entity, field, resolution) => post(`${base(workspaceId)}/resolutions`, { revision, scope, entity, field, resolution }),
  changeValuationDate: (workspaceId, revision, valuationDate) => post(`${base(workspaceId)}/valuation-date`, { revision, valuationDate }),
  price: (workspaceId, revision, idempotencyKey, marketSnapshotId) => post(`${base(workspaceId)}/price`, { revision, idempotencyKey, marketSnapshotId }),
  startLive: (workspaceId) => post(`${base(workspaceId)}/live`, {}),
  stopLive: async (subscriptionId) => {
    const response = await fetch(`/api/rates/live/${encodeURIComponent(subscriptionId)}`, { method: 'DELETE' });
    if (!response.ok && response.status !== 404) throw new Error(`The live subscription could not be stopped (${response.status}).`);
  },
  frames: (subscriptionId, onFrame, onBroken) => {
    const source = new EventSource(`/api/rates/live/${encodeURIComponent(subscriptionId)}/frames`);
    source.addEventListener('frame', (event) => onFrame(JSON.parse((event as MessageEvent<string>).data) as LiveFrame));
    // The browser would reconnect by itself, and a second reader of one subscription's frames is refused by
    // nobody but would split them; a broken stream is told instead, and the page starts again.
    source.onerror = () => { source.close(); onBroken(); };
    return { close: () => source.close() };
  },
  capture: (subscriptionId, frame, idempotencyKey) => post(`/api/rates/live/${encodeURIComponent(subscriptionId)}/capture`, {
    subscriptionRevision: frame.subscriptionRevision, inputRevision: frame.inputRevision, marketSnapshotId: frame.market.snapshotId, idempotencyKey,
  }),
};
