import { readTerms, readsAs, termCompletions, termParts, type ProductDescriptor } from './products';

// A non-financial product, as the server's kernel test has (ADR-0008, CO01): nothing here leans on rates.
const reading: ProductDescriptor = {
  id: 'weather.reading', version: '1.0.0', app: 'weather', scope: 'readings', words: 'reading', title: 'Station reading',
  fields: [
    { name: 'station', title: 'Station', form: 'choice', required: true, unnamed: true, choices: { north: 'NORTH', n: 'NORTH', south: 'SOUTH' } },
    { name: 'window', title: 'Window', form: 'period', required: true, unnamed: true },
    { name: 'volume', title: 'Volume', form: 'amount', required: false, unnamed: true },
    { name: 'from', title: 'First', form: 'choice', required: false, unnamed: true, sequence: 'pair', choices: { a: 'A', b: 'B' } },
    { name: 'to', title: 'Second', form: 'choice', required: false, unnamed: true, sequence: 'pair', choices: { a: 'A', b: 'B' } },
    { name: 'lag', title: 'Lag', form: 'period', required: false, unnamed: false, aliases: ['l'] },
  ],
};
const field = (name: string) => reading.fields.find((f) => f.name === name)!;

describe('reading typed words against a description (ADR-0008 §3)', () => {
  it('reads m as months and never as millions, so a period and an amount never share a word', () => {
    expect(readsAs(field('window'), '6m')).toBe(true);
    expect(readsAs(field('volume'), '6m')).toBe(false);
    expect(readsAs(field('volume'), '500mm')).toBe(true);
    expect(readsAs(field('volume'), '250')).toBe(true);
    // A bare full amount is refused by the server (owner, 2026-11-04), so the line does not read it as one.
    expect(readsAs(field('volume'), '3000000000')).toBe(false);
  });

  it('fills fields by form in any order, a sequence in its order, and names what it cannot place', () => {
    const { filled, unread } = readTerms(reading, ['b', '6m', 'n', 'l=2d', 'a', 'purple']);
    expect(Object.fromEntries(filled)).toEqual({ from: 'b', window: '6m', station: 'n', lag: '2d', to: 'a' });
    expect(unread).toEqual(['purple']);
  });

  it('offers what the next word could be: each choice once, a sequence only at its next field, and name= for named fields', () => {
    const offered = termCompletions(reading, ['north']).map((c) => c.value);
    expect(offered).toContain('1y');
    expect(offered).toContain('a');
    expect(offered).toContain('lag=');
    expect(offered).not.toContain('north');
    expect(termCompletions(reading, []).filter((c) => c.detail === 'Station').map((c) => c.value)).toEqual(['north', 'south']);
  });

  it('shows the required fields not yet typed', () => {
    expect(termParts(reading, ['south'])).toEqual([{ name: 'station', value: 'south', required: true }, { name: 'window', required: true }]);
  });
});
