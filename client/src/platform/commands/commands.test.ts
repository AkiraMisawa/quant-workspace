import { assertDistinct, execute, rankCommands, type Command } from './commands';

const command = (title: string, extra: Partial<Command> = {}): Command => ({ id: title, title, group: 'G', run: vi.fn(), ...extra });

describe('commands', () => {
  it('ranks a title typed in full above a longer title that contains it', () => {
    const titles = rankCommands('Show as Station summary', [command('Show as Station summary (compact)'), command('Show as Station summary')]).map((c) => c.title);
    expect(titles).toEqual(['Show as Station summary', 'Show as Station summary (compact)']);
  });

  it('matches by every word across title, group and keywords', () => {
    const c = command('Run Station summary', { group: 'Workflows', keywords: ['workflow.sample.station-summary'] });
    expect(rankCommands('workflows station', [c])).toEqual([c]);
    expect(rankCommands('rates', [c])).toEqual([]);
  });

  it('does not run an unavailable command and says why', () => {
    const c = command('Run', { unavailable: 'The context is not valid.' });
    expect(execute(c)).toBe('The context is not valid.');
    expect(c.run).not.toHaveBeenCalled();
  });

  it('refuses two commands on one shortcut, whatever the case', () => {
    expect(() => assertDistinct([command('a', { shortcut: 'mod+shift+G' }), command('b', { shortcut: 'Mod+Shift+g' })])).toThrow(/bound to both a and b/);
  });
});
