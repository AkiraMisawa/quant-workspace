// The Rates line pricer's wire shapes, as the server's RatesWorkspaces and RatesApi answer them
// (server/src/Workbench.Samples.Rates, server/src/Workbench.Web/RatesApi.cs). Figures are simulated and the
// market synthetic; the shapes are the contract.

import type { RunOrigin } from '../../platform/contracts';

export type Resolution = 'reset' | 'keep' | 'takeSource' | 'acknowledge';

export interface LineView {
  readonly line: string;
  /** The line's effective fields by their flat names (`end`, `leg1.notional`, …); empty is null. */
  readonly fields: Readonly<Record<string, unknown>>;
  /** The fields the user set. */
  readonly userFields: readonly string[];
  readonly priceable: boolean;
  /** The leg left without a quote, which pricing solves at par. */
  readonly parLeg: number | null;
  readonly spot: string | null;
  /** The convention profiles the legs resolved to, as `id@version`. */
  readonly profiles: readonly string[];
  /** Why the line cannot be priced as it stands. */
  readonly issues: readonly string[];
  /** Edits held back on the line or its cashflows, as `entity field: conflict|lost`. */
  readonly held: readonly string[];
}

export interface HeldEdit {
  readonly scope: string;
  readonly entity: string;
  readonly field: string;
  readonly value: unknown;
  readonly source: Readonly<Record<string, unknown>> | null;
  readonly acknowledged: boolean;
}

export interface CarrySummary { readonly carried: number; readonly conflicted: number; readonly lost: number }

export interface RatesWorkspaceView {
  readonly workspaceId: string;
  readonly revision: number;
  readonly valuationDate: string;
  readonly lines: readonly LineView[];
  readonly conflicts: readonly HeldEdit[];
  readonly lost: readonly HeldEdit[];
  /** What the last re-derivation of the schedules did to cashflow edits, if any moved. */
  readonly rederived: CarrySummary | null;
  readonly runs: readonly { readonly runId: string; readonly revision: number }[];
  /** The workspace's live subscription: the running one to attach to, or the last one stopped; absent when it never ran. */
  readonly live?: LiveStatus;
}

/**
 * A workspace's live state (owner, 2026-10-27): one subscription per workspace on the server, which pages attach
 * to; stopped by the user, or once nobody has watched it for the idle limit. A stopped one is never resumed.
 */
export interface LiveStatus {
  readonly subscriptionId: string;
  readonly running: boolean;
  readonly viewers: number;
  /** Absent while it runs: the server leaves an empty value out. */
  readonly stoppedBy?: 'user' | 'idle' | 'deleted';
  readonly stoppedAt?: string;
  /** As .NET writes a TimeSpan: `hh:mm:ss`. */
  readonly idleLimit: string;
  readonly lastFrame?: LiveFrame;
  /** Who started it and, when the user stopped it, from where: the page or the AI acting for the user (ADR-0005). */
  readonly startedFrom: RunOrigin;
  readonly stoppedFrom?: RunOrigin;
}

export interface PeriodView {
  readonly entity: string;
  readonly leg: number;
  readonly accrualStart: string;
  readonly accrualEnd: string;
  readonly paymentDate: string;
  readonly notional: number;
  readonly rate: number | null;
  readonly yearFraction: number;
  readonly userFields: readonly string[];
}

export interface ExchangeView {
  readonly leg: number;
  readonly kind: 'initial' | 'final';
  readonly paymentDate: string;
  readonly amount: number;
  readonly currency: string;
}

export interface CashflowsView {
  readonly line: string;
  readonly revision: number;
  readonly periods: readonly PeriodView[];
  readonly exchanges: readonly ExchangeView[];
}

export interface MarketUpdate {
  readonly sequence: number;
  readonly snapshotId: string;
  readonly asOf: string;
  readonly origin: string;
}

export interface MarketView {
  readonly snapshotId: string;
  readonly sequence: number;
  readonly asOf: string;
  readonly origin: string;
}

export interface Measure { readonly value: number; readonly unit: string }

export interface RowResult {
  readonly rowKey: string;
  readonly measures: Readonly<Record<string, Measure>>;
  readonly failure?: string | null;
}

export interface NotPricedLine { readonly line: string; readonly reasons: readonly string[] }

/** One frame a live subscription delivered, with what it was taken against (docs/07 §5). */
export interface LiveFrame {
  readonly subscriptionId: string;
  readonly subscriptionRevision: number;
  readonly inputRevision: number;
  readonly market: MarketUpdate;
  readonly completedAt: string;
  readonly provider: string;
  readonly calculationMode: string;
  readonly rows: readonly RowResult[];
  readonly notPriced: readonly NotPricedLine[];
}
