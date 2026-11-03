import { assertDistinct, completionsOf, execute, finish, rankCommands, readLine, take, type Command } from './commands';

const command = (words: string, extra: Partial<Command> = {}): Command => ({ id: words, words, title: words, group: 'G', run: vi.fn(), ...extra });

const irs = command('new irs', {
  args: [
    { name: 'currency', required: true, complete: () => [{ value: 'JPY' }, { value: 'USD' }] },
    { name: 'index', required: true, complete: (g) => (g.currency === 'USD' ? [{ value: 'SOFR' }] : [{ value: 'TONA' }]) },
    { name: 'end', required: true, check: (v) => (/^\d+y$/.test(v) ? undefined : 'a tenor such as 10y') },
    { name: 'rate', required: false },
  ],
});

describe('finding a command by typing (ADR-0006)', () => {
  it('puts the words typed in full above longer words that start with them', () => {
    expect(rankCommands('price', [command('price all'), command('price')]).map((c) => c.words)).toEqual(['price', 'price all']);
  });

  it('finds a command from the start of each of its words, in order', () => {
    expect(rankCommands('op pr', [command('price'), command('open pricing db')]).map((c) => c.words)).toEqual(['open pricing db']);
    expect(rankCommands('zz', [command('open pricing db')])).toEqual([]);
  });

  it('ranks what the user runs most first among equal matches, so a few letters are enough', () => {
    const ranked = rankCommands('p', [command('previous panel'), command('price')], (id) => (id === 'price' ? 3 : 0));
    expect(ranked[0]!.words).toBe('price');
  });

  it('lists only what can run now for ?', () => {
    expect(rankCommands('?', [command('price', { unavailable: 'No workspace.' }), command('new irs')]).map((c) => c.words)).toEqual(['new irs']);
  });

  it('does not run an unavailable command and says why', () => {
    const c = command('price', { unavailable: 'Open an XVA or Rates workspace to price it.' });
    expect(execute(c)).toBe('Open an XVA or Rates workspace to price it.');
    expect(c.run).not.toHaveBeenCalled();
  });

  it('refuses two commands with the same words, or on one chord', () => {
    expect(() => assertDistinct([command('price', { id: 'a' }), command('price', { id: 'b' })])).toThrow(/name both a and b/);
    expect(() => assertDistinct([command('a', { shortcut: 'mod+shift+G' }), command('b', { shortcut: 'Mod+Shift+g' })])).toThrow(/bound to both a and b/);
  });
});

describe('completing arguments one slot at a time (ADR-0006 §4)', () => {
  it('chooses a command once its words are followed by a space, and offers the first slot', () => {
    expect(readLine('new irs', [irs]).kind).toBe('choosing');
    const reading = readLine('new irs ', [irs]);
    expect(reading.kind === 'arguments' && reading.slot?.name).toBe('currency');
    expect(completionsOf(reading, [irs]).values.map((v) => v.value)).toEqual(['JPY', 'USD']);
  });

  it('offers values that follow from the arguments before them', () => {
    expect(completionsOf(readLine('new irs USD ', [irs]), [irs]).values.map((v) => v.value)).toEqual(['SOFR']);
  });

  it('takes a completion into the line and moves to the next slot', () => {
    const text = 'new irs U';
    const reading = readLine(text, [irs]);
    expect(take(text, reading, { value: 'USD' })).toBe('new irs USD ');
  });

  it('names every missing argument and runs nothing', () => {
    const reading = readLine('new irs JPY', [irs]);
    expect(reading.kind).toBe('arguments');
    if (reading.kind !== 'arguments') return;
    expect(finish(reading)).toEqual({ problems: ['Missing: index, end.'] });
  });

  it('reads a line typed in one go, optional values by name, and checks each value', () => {
    const ok = readLine('new irs JPY TONA 10y rate=0.5', [irs]);
    if (ok.kind !== 'arguments') throw new Error('not chosen');
    expect(finish(ok)).toEqual({ args: { currency: 'JPY', index: 'TONA', end: '10y', rate: '0.5' } });

    const bad = readLine('new irs JPY TONA ten spread=1 extra', [irs]);
    if (bad.kind !== 'arguments') throw new Error('not chosen');
    expect(finish(bad)).toEqual({ problems: ['new irs has no option spread.', 'end: a tenor such as 10y'] });
  });

  it('keeps the rest of the line for a request to the AI', () => {
    const ai = command('ai', { args: [{ name: 'request', required: true, rest: true }] });
    const reading = readLine('ai make a 10y swap  and tick it', [ai]);
    if (reading.kind !== 'arguments') throw new Error('not chosen');
    expect(finish(reading)).toEqual({ args: { request: 'make a 10y swap  and tick it' } });
  });
});
