# ADR-0006: One command line, found by typing rather than remembered

Status: PROPOSED 2026-10-27, for the owner's decision. Raised by the owner: shortcuts will run out, and Bloomberg's
function codes cannot be remembered; "I want something intuitive" (owner, 2026-10-27).

## Context

ADR-0002 makes the product keyboard-first and asks for one command system. Today every command carries an optional
chord (`mod+alt+R`, `mod+shift+G`, …), and the palette (Ctrl+K) finds commands by fuzzy match on title and keywords.
Chords are a fixed, small space shared by every module: each new domain application takes some, browsers and the
operating system take others, and nobody can remember more than a handful. Criterion C27 adds capabilities without
touching the shell, so the number of commands only grows.

Bloomberg avoids running out by typing short mnemonic codes (`DES`, `SWPM`) after a loaded security. The space never
runs out, but every code has to be learned. The owner does not want that.

## Decision

1. **One command line is the way to every action.** It opens with Ctrl+K or `/` from anywhere, and it is the same
   surface as today's palette. Buttons, the command line and the AI reach the same command (ADR-0005), so none of
   them can do something the others cannot.
2. **Commands are found by what they do, in words the user already has.** A query is matched against the command's
   title, its verb and object (`price`, `new`, `start live`; `swap`, `trade`, `workspace`), and its keywords in
   English and Japanese (`プライス`, `スワップ`, `ティッキング`). Nothing has to be known in advance: typing shows the
   candidates as you go, each with what it will act on and why it is unavailable if it is.
3. **The current context is the default target.** The active panel and its selection are the "loaded" object: `price`
   in a Rates workspace prices that workspace, and the line shows it ("Price Rates · W-12, revision 9"). A command
   that needs another target asks for it as an argument.
4. **Arguments are completed one at a time from the capability's contract.** After choosing a command, the line shows
   its arguments as slots (`new irs` → currency, index, end, notional, direction, …). Each slot offers only valid
   values, taken from the data (the configured conventions, a counterparty search, the workspace's lines). A typed
   value is checked as it is typed. Missing required arguments are asked for by name; none is filled in silently.
   Free text that already holds the arguments (`irs jpy 10y 30億 pay`) is parsed into the same slots and shown before
   Enter, so the user sees what will run.
5. **Short forms are learned, not taught.** The ranking favours what this user ran often and recently, so after a
   few uses `pr` is enough for Price. A user may also save an alias of their own. No alias is required to use a
   command, and none is shipped as something to memorise.
6. **The AI is the last entry.** When nothing matches well, the line offers "Ask the AI: …" with the text as typed,
   which goes to the AI pane and ends in the same capabilities.
7. **Chords are kept for the shell only:** opening the command line, moving between panels, focusing the grid, and
   closing a panel. A domain command gets no chord by default; a user may bind one of their own. Grid keys stay
   ExGrid's.

## Consequences

- A capability that is a command declares its verb, object, keywords (both languages) and argument completions
  beside its contract. The shell's command list reads them; no module edits the shell (C27).
- The existing chords for New XVA / New Rates / Price XVA / Pricing DB become defaults a user can change, then are
  dropped as shipped defaults once the command line covers them.
- Ranking by frequency and saved aliases are per user and stored server-side with the user's settings (not
  `localStorage`), so they follow the user between browsers.
- Help is the command line itself: `?` lists what can be done in the current context.

## Rejected

- **More chords, or chord sequences (Ctrl+K then R then P).** Still a code to learn, and still finite per module.
- **Bloomberg-style mnemonic codes as the primary input.** Unlimited, but each must be remembered (the owner's point).
- **Only natural language through the AI.** Slower, needs a model for every action, and an exact command should not
  depend on one.
