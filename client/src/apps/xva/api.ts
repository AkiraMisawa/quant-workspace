import { call } from '../../platform/api';
import type { RunRecord } from '../../platform/contracts';
import type { CarrySummary, EffectiveTrade, Registration, Agreement, Resolution, RowPage, ScopeDrop, XvaWorkspaceView } from './contracts';

export interface RowQuery {
  readonly start: number;
  readonly count: number;
  readonly sorts: readonly { readonly column: string; readonly direction: 'ascending' | 'descending' }[];
}

const base = (workspaceId: string) => `/api/xva/workspaces/${encodeURIComponent(workspaceId)}`;
const post = <T>(path: string, body: unknown, signal?: AbortSignal) =>
  call<T>(path, { method: 'POST', body: JSON.stringify(body), ...(signal === undefined ? {} : { signal }) });

/** The XVA workspace endpoints. Every command names the revision it was taken against. */
export interface XvaApi {
  registrations(): Promise<readonly (Registration & { readonly agreement: Agreement })[]>;
  create(registration: string, valueDate: string, portfolioSize: number): Promise<XvaWorkspaceView>;
  view(workspaceId: string): Promise<XvaWorkspaceView>;
  rows(workspaceId: string, query: RowQuery, signal: AbortSignal): Promise<RowPage>;
  edit(workspaceId: string, revision: number, entity: string, field: string, value: unknown): Promise<XvaWorkspaceView>;
  resolve(workspaceId: string, revision: number, entity: string, field: string, resolution: Resolution): Promise<XvaWorkspaceView>;
  changeValueDate(workspaceId: string, revision: number, valueDate: string): Promise<{ readonly workspace: XvaWorkspaceView; readonly carried: CarrySummary }>;
  previewCounterpartyChange(workspaceId: string): Promise<ScopeDrop>;
  changeCounterparty(workspaceId: string, revision: number, registration: string, confirmedCount: number): Promise<{ readonly workspace: XvaWorkspaceView; readonly carried: CarrySummary }>;
  price(workspaceId: string, revision: number, idempotencyKey: string): Promise<RunRecord>;
}

export const httpXvaApi: XvaApi = {
  registrations: () => call('/api/xva/registrations'),
  create: (registration, valueDate, portfolioSize) => post('/api/xva/workspaces', { registration, valueDate, portfolioSize }),
  view: (workspaceId) => call(base(workspaceId)),
  rows: (workspaceId, query, signal) => post(`${base(workspaceId)}/rows`, query, signal),
  edit: (workspaceId, revision, entity, field, value) => post(`${base(workspaceId)}/edits`, { revision, entity, field, value }),
  resolve: (workspaceId, revision, entity, field, resolution) => post(`${base(workspaceId)}/resolutions`, { revision, entity, field, resolution }),
  changeValueDate: (workspaceId, revision, valueDate) => post(`${base(workspaceId)}/value-date`, { revision, valueDate }),
  previewCounterpartyChange: (workspaceId) => call(`${base(workspaceId)}/counterparty-change`),
  changeCounterparty: (workspaceId, revision, registration, confirmedCount) =>
    post(`${base(workspaceId)}/counterparty`, { revision, registration, confirmedCount }),
  price: (workspaceId, revision, idempotencyKey) => post(`${base(workspaceId)}/price`, { revision, idempotencyKey }),
};

export type { EffectiveTrade };
