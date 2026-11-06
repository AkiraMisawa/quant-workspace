// The products a workspace app can create, as the server describes them (ADR-0008 §2, §7), and a reading of typed
// words against a description. The page reads words only to complete them and to show what the line would make; the
// server reads them again and decides (ADR-0008 §6), so nothing here creates, defaults or refuses a term.

export type FieldForm = 'period' | 'date' | 'periodOrDate' | 'amount' | 'percent' | 'choice' | 'flag' | 'number';

export interface ProductField {
  readonly name: string;
  readonly title: string;
  readonly form: FieldForm;
  readonly required: boolean;
  /** Typed without its name, read by its form. */
  readonly unnamed: boolean;
  /** Fields of one sequence take words of their form in the order declared. */
  readonly sequence?: string | null;
  /** A choice's words, each to the value it means. */
  readonly choices?: Readonly<Record<string, string>> | null;
  readonly aliases?: readonly string[] | null;
}

export interface ProductDescriptor {
  readonly id: string;
  readonly version: string;
  readonly app: string;
  readonly scope: string;
  /** What `new <words>` names it by. */
  readonly words: string;
  readonly title: string;
  readonly fields: readonly ProductField[];
}

// The server's forms (ProductReader.Parse). A form says how a word is read, never what it means; `m` is months, so an
// amount is bare (millions), `mm`, `bn` or `k`, and the two forms never read the same word.
const PERIOD = /^[1-9][0-9]{0,2}[dwmy]$/i;
const DATE = /^[0-9]{4}-[0-9]{2}-[0-9]{2}$/;
const AMOUNT = /^([0-9][0-9,]*(?:\.[0-9]+)?)(k|mm|bn)?$/i;
const PERCENT = /^-?[0-9]+(?:\.[0-9]+)?(%|bp)$/i;

/** Whether a word reads as the field's form. A bare amount of a million or more does not: it is refused (owner, 2026-11-04). */
export function readsAs(field: ProductField, word: string): boolean {
  switch (field.form) {
    case 'period': return PERIOD.test(word);
    case 'date': return DATE.test(word);
    case 'periodOrDate': return PERIOD.test(word) || DATE.test(word);
    case 'amount': {
      const m = AMOUNT.exec(word);
      if (m === null) return false;
      const n = Number(m[1]!.replaceAll(',', ''));
      return n > 0 && (m[2] !== undefined || n < 1e6);
    }
    case 'percent': return PERCENT.test(word);
    case 'choice': return field.choices?.[word.toLowerCase()] !== undefined;
    case 'flag': return word === 'true' || word === 'false';
    case 'number': return word !== '' && Number.isFinite(Number(word));
  }
}

/** What the words typed so far fill, field by field, and the words the reading could not place. */
export interface TermsReading {
  readonly filled: ReadonlyMap<string, string>;
  readonly unread: readonly string[];
}

/** Reads words as the server does (ADR-0008 §3): `name=value` by name or alias, any other word by the one field its form fits. */
export function readTerms(product: ProductDescriptor, words: readonly string[]): TermsReading {
  const filled = new Map<string, string>();
  const unread: string[] = [];
  const named = (name: string) => product.fields.find((f) => f.name.toLowerCase() === name || (f.aliases ?? []).some((a) => a.toLowerCase() === name));
  for (const word of words) {
    const eq = word.indexOf('=');
    if (eq > 0) {
      const field = named(word.slice(0, eq).toLowerCase());
      if (field === undefined || filled.has(field.name)) unread.push(word);
      else filled.set(field.name, word.slice(eq + 1));
      continue;
    }
    const candidates = product.fields.filter((f) => f.unnamed && !filled.has(f.name) && readsAs(f, word));
    const first = candidates[0];
    const sequence = first?.sequence ?? undefined;
    if (first === undefined || (candidates.length > 1 && (sequence === undefined || candidates.some((c) => c.sequence !== sequence)))) unread.push(word);
    else filled.set(first.name, word);
  }
  return { filled, unread };
}

const TENORS = ['1y', '2y', '3y', '5y', '7y', '10y', '15y', '20y', '30y'];

/**
 * What the word being typed could be: the words of each unfilled unnamed choice (one per value, the value's own word where it has one), tenors
 * for an unfilled unnamed period, and `name=` for each unfilled named field.
 */
export function termCompletions(product: ProductDescriptor, words: readonly string[]): { readonly value: string; readonly detail?: string }[] {
  const { filled } = readTerms(product, words);
  const out: { value: string; detail?: string }[] = [];
  const seen = new Set<string>();
  const add = (value: string, detail: string) => { if (!seen.has(value)) { seen.add(value); out.push({ value, detail }); } };
  for (const field of product.fields) {
    if (filled.has(field.name)) continue;
    if (field.unnamed && field.form === 'choice') {
      // A sequence offers only its next field, so leg 2's currency is not offered before leg 1's.
      if (field.sequence != null && product.fields.some((f) => f.sequence === field.sequence && !filled.has(f.name) && product.fields.indexOf(f) < product.fields.indexOf(field))) continue;
      const byValue = new Map<string, string>();
      for (const [word, value] of Object.entries(field.choices ?? {}))
        if (!byValue.has(value) || word === value.toLowerCase()) byValue.set(value, word);
      for (const word of byValue.values()) add(word, field.title);
    } else if (field.unnamed && (field.form === 'period' || field.form === 'periodOrDate')) {
      for (const tenor of TENORS) add(tenor, field.title);
    } else if (!field.unnamed) {
      add(`${field.name}=`, field.title);
    }
  }
  return out;
}

/** How the typed words fill the product, for the line to show: each field read, and the required ones not yet typed. */
export function termParts(product: ProductDescriptor, words: readonly string[]): { readonly name: string; readonly value?: string; readonly required: boolean }[] {
  const { filled, unread } = readTerms(product, words);
  return [
    ...product.fields.filter((f) => filled.has(f.name) || f.required).map((f) => ({ name: f.name, ...(filled.has(f.name) ? { value: filled.get(f.name)! } : {}), required: f.required })),
    ...unread.map((w) => ({ name: '?', value: w, required: false })),
  ];
}
