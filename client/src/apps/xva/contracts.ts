// The XVA workspace's wire shapes, as the server's XvaWorkspaces answers them (server/src/Workbench.Samples.Xva).
// The figures and identifiers are synthetic; the shapes are the contract.

export type Ownership = 'source' | 'automatic' | 'user';

export interface EffectiveTrade {
  readonly tradeId: string;
  readonly isNew: boolean;
  readonly product: string;
  readonly currency: string;
  readonly notional: number;
  readonly maturity: string;
  readonly fixedRate: number | null;
  readonly schedule: string | null;
  readonly lastFixing: number | null;
  readonly csa: string;
  readonly csaOwner: Ownership;
  readonly curve: string;
  readonly curveOwner: Ownership;
  readonly before: boolean;
  readonly after: boolean;
  /** Why this row cannot be priced as it stands; Price is refused while any priced row has one. */
  readonly issues: readonly string[];
  /** Edits held back on this row, as `field: conflict` or `field: lost`. */
  readonly held: readonly string[];
  /** The fields the user set on this row. */
  readonly overridden: readonly string[];
}

export interface Registration { readonly key: string; readonly code: string; readonly legalEntity: string; readonly name: string }

export interface Agreement {
  readonly maId: string;
  readonly nettingSetId: string;
  readonly registrationKey: string;
  readonly csas: readonly { readonly csaId: string; readonly currency: string; readonly curve: string }[];
  readonly mainCsaId: string | null;
  readonly collateralised: boolean;
  readonly noCsa: string;
}

export interface EffectiveCsa { readonly csaId: string; readonly currency: string; readonly curve: string; readonly curveOwner: Ownership }

export interface EffectiveCredit { readonly cvaRecovery: number | null; readonly cvaRecoveryOwner: Ownership; readonly cdsRecovery: number }

export interface HeldEdit {
  readonly scope: string;
  readonly entity: string;
  readonly field: string;
  readonly value: unknown;
  readonly source: Readonly<Record<string, unknown>> | null;
  readonly acknowledged: boolean;
}

export interface XvaWorkspaceView {
  readonly workspaceId: string;
  readonly revision: number;
  readonly context: { readonly registration: string; readonly valueDate: string; readonly portfolioSize: number };
  readonly sourceVersion: string;
  readonly registration: Registration;
  readonly agreement: Agreement;
  readonly csas: readonly EffectiveCsa[];
  readonly credit: EffectiveCredit;
  readonly trades: number;
  readonly overrides: number;
  readonly conflicts: readonly HeldEdit[];
  readonly lost: readonly HeldEdit[];
  readonly runs: readonly { readonly runId: string; readonly revision: number }[];
}

export interface RowPage {
  readonly rows: readonly EffectiveTrade[];
  readonly start: number;
  readonly totalCount: number;
  readonly orderToken: string;
  readonly revision: number;
}

export interface CarrySummary { readonly carried: number; readonly conflicted: number; readonly lost: number }

export interface ScopeDrop { readonly scope: string; readonly count: number; readonly byKind: Readonly<Record<string, number>> }

export type Resolution = 'reset' | 'keep' | 'takeSource' | 'acknowledge';
