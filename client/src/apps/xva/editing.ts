import { ApiError } from '../../platform/api';
import type { Resolution, XvaWorkspaceView } from './contracts';

// From what the user typed in a grid cell to the value the server is sent. Only the form is checked here;
// what a value means (an eligible CSA, an available curve) is the server's, and shows on the row.

export type Parsed = { readonly ok: true; readonly value: unknown } | { readonly ok: false; readonly message: string };

const fail = (message: string): Parsed => ({ ok: false, message });

/**
 * A number as typed: digits with an optional sign and decimal point, and thousands commas only where every
 * group has three digits. Anything else is refused rather than read one way of several.
 */
export function parseNumber(text: string): number | undefined {
  const t = text.trim();
  if (!/^-?(\d{1,3}(,\d{3})+|\d+)(\.\d+)?$/.test(t)) return undefined;
  const value = Number(t.replaceAll(',', ''));
  return Number.isFinite(value) ? value : undefined;
}

/** Excel's words for a Boolean cell, in any case. */
export function parseBoolean(text: string): boolean | undefined {
  const t = text.trim().toUpperCase();
  return t === 'TRUE' ? true : t === 'FALSE' ? false : undefined;
}

/** The editable columns of the trades grid, and how each reads what was typed. */
export const tradeFields: Readonly<Record<string, (text: string) => Parsed>> = {
  notional: (text) => {
    const n = parseNumber(text);
    return n === undefined || n === 0 ? fail('Type a non-zero number, such as 1,000,000.') : { ok: true, value: n };
  },
  fixedRate: (text) => {
    if (text.trim() === '') return { ok: true, value: null };
    const n = parseNumber(text);
    return n === undefined ? fail('Type a rate as a decimal, such as 0.0125, or leave it empty.') : { ok: true, value: n };
  },
  schedule: (text) => {
    const t = text.trim().toUpperCase();
    return t === '3M' || t === '6M' ? { ok: true, value: t } : fail('Type 3M or 6M.');
  },
  csa: (text) => (text.trim() === '' ? fail('Type a CSA identifier.') : { ok: true, value: text.trim() }),
  curve: (text) => (text.trim() === '' ? fail('Type a curve identifier.') : { ok: true, value: text.trim() }),
  before: (text) => {
    const b = parseBoolean(text);
    return b === undefined ? fail('Type TRUE or FALSE.') : { ok: true, value: b };
  },
  after: (text) => {
    const b = parseBoolean(text);
    return b === undefined ? fail('Type TRUE or FALSE.') : { ok: true, value: b };
  },
};

/** A recovery as typed: empty stays empty (the CDS recovery then applies), else a number from 0 up to 1. */
export function parseRecovery(text: string): Parsed {
  if (text.trim() === '') return { ok: true, value: null };
  const n = parseNumber(text);
  return n === undefined || n < 0 || n >= 1 ? fail('Type a recovery from 0 up to 1, or leave it empty.') : { ok: true, value: n };
}

/** A change to send: a value set on a field, or a held edit resolved. */
export type PendingEdit =
  | { readonly entity: string; readonly field: string; readonly value: unknown; readonly resolution?: undefined }
  | { readonly entity: string; readonly field: string; readonly resolution: Resolution };

/**
 * Sends one user's edits one at a time, each against the revision the previous one produced. Edits typed
 * faster than the server answers therefore chain on this session's own changes; a revision moved by anyone
 * else is still refused by the server (ADR-0003, spine 6), and every edit queued behind the refusal is
 * dropped with it rather than sent against a workspace the user has not seen.
 */
export class EditQueue {
  #revision: number;
  #tail: Promise<void> = Promise.resolve();
  #broken = false;

  constructor(
    revision: number,
    private readonly send: (revision: number, edit: PendingEdit) => Promise<XvaWorkspaceView>,
    private readonly applied: (view: XvaWorkspaceView) => void,
    private readonly refused: (edit: PendingEdit, error: unknown) => void,
  ) {
    this.#revision = revision;
  }

  get revision(): number {
    return this.#revision;
  }

  /** The workspace was read again (after a refusal or another command): later edits chain on this revision. */
  rebase(revision: number): void {
    this.#revision = revision;
    this.#broken = false;
  }

  push(edit: PendingEdit): Promise<void> {
    this.#tail = this.#tail.then(async () => {
      if (this.#broken) {
        this.refused(edit, new Error('An earlier edit was refused; this one was not sent.'));
        return;
      }
      try {
        const view = await this.send(this.#revision, edit);
        this.#revision = view.revision;
        this.applied(view);
      } catch (error) {
        if (error instanceof ApiError && error.envelope.code === 'RevisionMoved') this.#broken = true;
        this.refused(edit, error);
      }
    });
    return this.#tail;
  }
}
