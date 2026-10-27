# ADR-0004: Entities the user creates, and sources derived from the workspace's own values

Status: ACCEPTED 2026-10-27 by the owner, after review against ADR-0003 §7 by the XVA thread; for the Rates line pricer (C37 to C42). Extends ADR-0003 §6 and §7; changes
nothing XVA does today.

## Context

ADR-0003's workspace has one acquired source version, and every edit overrides a field that version holds.
A value date change acquires a new version and runs the carry check (§7) on every override.

The line pricer needs two things that model does not have:

1. **Rows the user creates.** A line exists because the user added it; no source holds it. Its terms are all
   the user's.
2. **A source that is a function of the workspace.** A line's cashflow schedule is generated from the line's
   effective terms (C39). The user customises cashflows on top of it. When a term changes, the schedule is
   generated again, and the customisations must go through the same carry check as an override after a value
   date change: carried, conflicted or lost, and Price refused while one is unresolved.

Keeping these inside the Rates module would give Rates an edit store of its own, against ADR-0003 §6 (one
generic, append-only log for every domain).

## Decision

**One source, three layers.** An edit is read against its derivation scope's source if its scope is one,
against its created entity if the user created it, and otherwise against the acquired version. An override's
`BaseVersion` names the layer it came from (`created:<seq>`, a derived version, or the acquired version). A
re-acquisition checks only the overrides of the acquired layer; the others are not in the new version and are
not lost by it.

**Created entities.** Two kernel edit kinds, `Create` and `Remove`. `Create` names an entity the workspace
itself holds, its scope, and the module's initial field values for it, which act as its source values.
`Remove` ends it, and its own overrides end with it, counted in the log entry's note: the user removed them,
so they are not lost targets to acknowledge (a lost target is something the source took away, ADR-0003 §7).
Dropping a scope drops the entities created in it, and the count the user confirms includes them. Nothing is
deleted from the log.

**Derived sources.** A module may register a derivation: a pure function from the workspace's effective
values to a source version for derived entities, versioned per derivation scope (for Rates, one scope per
line, versioned by its resolved terms, its convention profiles and the generator's version). After every
accepted command the kernel derives again. In each scope whose version changed, the overrides, conflicts and
lost edits go through `Carry.Check` exactly as after a re-acquisition, a scope that is gone leaving its edits
without a target. Each moved scope writes a `Derive` entry with its scope and new version in the same command,
so the log explains why an edit became a conflict (ADR-0003 §6); the command's answer carries the summed
`CarrySummary`. Conflict groups apply to derived fields as to any other, and the module's priced predicate for
Price names derived entities, so their conflicts block Price. Derived sources are not stored: they are a pure
function of the stored state, derived again on load.

**Identity of a derived entity is the module's, and must not move with what it is derived from.** For a
cashflow period it is its leg and its unadjusted start and end dates, not its ordinal: a maturity moved from
5y to 6y keeps every existing period's identity and adds one, while a moved start date makes the old periods
lost targets rather than quietly re-pointing their edits at other periods.

The kernel still knows nothing of lines, legs or schedules. It stores opaque entities, scopes and fields.

## Consequences

- A derivation runs on every edit. For Rates that is one line's schedule, so its cost is bounded by the
  line, not the workspace; it is measured with the slice, not assumed.
- XVA does not register a derivation and does not create entities, so nothing it does changes. Its CSAs that a
  user creates (ADR-0003 §7's table) can later use `Create` instead of a module-specific path.
- The SQLite store keeps the same tables: `Create` and `Remove` are rows of `EditLog` like any other kind.
