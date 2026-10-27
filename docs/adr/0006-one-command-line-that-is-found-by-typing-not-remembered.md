# ADR-0006: One command line, found by typing rather than remembered

Status: ACCEPTED 2026-10-27 by the owner, with the owner's corrections of the same day (recorded under "Changed from
the proposal"). Raised by the owner: shortcuts will run out, and Bloomberg's function codes cannot be remembered; "I
want something intuitive".

## Context

ADR-0002 makes the product keyboard-first and asks for one command system. Until now every command could carry a chord
(`mod+alt+R`, `mod+alt+P`, …), and the palette (Ctrl+K) found commands by fuzzy match on their title. Chords are a
small fixed space shared by every module: each domain application takes some, browsers and the operating system take
others, and nobody remembers more than a handful. Criterion C27 adds capabilities without touching the shell, so the
number of commands only grows.

Bloomberg avoids running out by typing short mnemonic codes (`DES`, `SWPM`) after a loaded security. The space never
runs out, but every code has to be learned. The owner does not want that.

## Decision

1. **One command line is the way to every action.** It opens with Ctrl+K from anywhere. Buttons, the command line and
   the AI reach the same command (ADR-0005), so none of them can do something the others cannot.
2. **It is not the AI chat.** The AI pane will have slash commands of its own, about the conversation. They are a
   separate surface: the command line does not converse, and `/` does not open it.
3. **Commands are English words for what they do**: a verb and its object (`price`, `new irs`, `live start`,
   `capture`, `open pricing db`). Nothing has to be known in advance.
4. **Completion is shown for the command and for each argument.** As the user types, the line lists the commands
   that match, each with what it will act on, its arguments, and why it is unavailable if it is. After a command is
   chosen, the line shows its arguments as slots and lists the valid values of the slot being typed, taken from the
   data (configured conventions, a counterparty search, the workspace's lines). Tab takes the highlighted completion;
   Enter runs. Missing required arguments are named and nothing runs; no value is filled in silently. A line typed in
   one go (`new irs JPY TONA 10y 3000000000 pay`) is read into the same slots and shown before Enter.
5. **The current context is the default target.** The active panel and its selection are the "loaded" object: `price`
   in a Rates workspace prices that workspace, and the line says so ("Rates · W-12, revision 9"). A command that needs
   another target takes it as an argument.
6. **Short forms are learned, not taught.** The ranking favours what this user ran often and recently, so after a few
   uses `pr` is enough for `price`. There are no user-defined aliases.
7. **`ai` is a command.** `ai <request>` hands the request, with the current context, to the AI pane, which ends in the
   same capabilities. When nothing matches what was typed, `ai <what was typed>` is offered last.
8. **Chords are kept for the shell only:** opening the command line, moving between panels, focusing the grid, editing
   the context, and closing a panel. Domain commands have no chord. Grid keys stay ExGrid's.

## Consequences

- A command declares its words, its arguments and where each argument's completions come from. A command a module
  registers through the catalog gets the same treatment, so no module edits the shell (C27).
- The chords for New XVA, Price XVA, New Rates, Pricing DB and the theme toggle are removed now, before the command
  line covers everything they did (the owner, 2026-10-27).
- Ranking by frequency is per user. Until the user settings store exists (with sign-in, C30), it is kept in the
  browser and says so; it moves to the server then.
- `?` lists what can be done in the current context.

## Changed from the proposal

The owner's corrections, 2026-10-27: the command line and the AI chat's slash commands are separate (so `/` no longer
opens the line); commands are English only, not English and Japanese; completion is shown for commands and arguments
alike; existing chords go now rather than after replacement; no user-defined aliases; an `ai` command is provided.

## Rejected

- **More chords, or chord sequences (Ctrl+K then R then P).** Still a code to learn, and still finite per module.
- **Bloomberg-style mnemonic codes as the primary input.** Unlimited, but each must be remembered (the owner's point).
- **Only natural language through the AI.** Slower, needs a model for every action, and an exact command should not
  depend on one.
- **User-defined aliases.** Not wanted by the owner; frequency ranking gives short forms without a second vocabulary.
