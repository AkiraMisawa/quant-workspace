# Slice 02: the XVA workspace, from editing to the Pricing DB

Status: plan, 2026-10-27. Follows slice 01 and implements the start of P2
(`docs/12-implementation-plan.md`). Decisions it rests on: ADR-0003 §2, §6 and §7 (§7 still OPEN, built
as proposed and changed if the owner changes it), `docs/03-xva-domain.md`, `docs/05-execution-history.md`
§7.

All data and every figure are synthetic. The analytics provider is a deterministic fake whose numbers
mean nothing financially; every screen and record labels them simulated. Nothing here claims a real CVA
or FVA.

## Goal

A user loads synthetic new trades against a counterparty, edits CSAs, curves and Before/After on the
server-authoritative workspace through ExGrid, prices CVA and FVA (Before, After, incremental), changes
the value date and prices again, and finds both Runs in the Pricing DB's XVA view. The workspace stays
responsive at 10,000 and 50,000 existing trades.

## What is built

**Synthetic XVA module** (`Workbench.Samples.Xva`, no kernel reference to it)
- Sources: counterparty registrations, two of them sharing a code under different legal entities (XV01);
  agreements, netting sets and CSAs, including an uncollateralised `nocsa_{ma id}` agreement with a
  configured default curve (XV16); existing portfolios of IRS and FX forwards, generated to 50,000 rows
  per counterparty on request; draft new trades; CDS recovery and an empty CVA recovery (XV15).
- Every source answer is versioned by value date: a trade acquired for another date is another version
  (ADR-0003 §6), and a seeded "fixing" changes some trades' fields between dates.
- Edit kinds, scope keys and conflict groups registered as ADR-0003 §7 describes.
- Capabilities `xva.prepare-context`, `xva.price` (fake provider, CVA and FVA per side) and the
  `xva.case-result` contract; the Pricing DB projection over it.

**Kernel additions** (generic, no XVA knowledge)
- Workspace service: append-only edit log, current state, revision with optimistic concurrency, field
  ownership (Source / Automatic / User), the carry check of ADR-0003 §7, and freeze to a Run.
- Paged effective rows: the server materialises, filters, sorts and pages; the client asks for a window.
- Run origin and relation links; history projections registered by modules.
- Stores behind ports. The edit log and workspaces go to SQLite for the local demonstrator (D04, `contracts/storage.sql`), after an in-memory first cut; the production database stays open.

**Client**
- A workspace panel: context bar for value date and counterparty registration, the existing and new
  trade grids on `GridSource` paging, edits sent as typed commands, conflict and lost-target markers, a
  summary after re-acquisition, Price.
- The Pricing DB page: all Runs, relation links, one domain view at a time (XVA first).

## Acceptance

| ID | Evidence |
|---|---|
| XV01, XV02, XV09, XV11, XV12, XV13, XV14, XV15, XV16 | xUnit on the workspace service and the module; Playwright for XV02, XV14, XV16 |
| EX01 (Before/After part), EX06 | xUnit on the Run; Playwright shows a failed case unavailable |
| ADR-0003 §6 | Edit log survives a page reload; counterparty change asks before dropping edits |
| ADR-0003 §7 | Each of the four outcomes (lost target, carried, conflict, invalid) after a value date change; Price refused while a priced trade has a conflict |
| Pricing DB | Runs from the pricer and from a workflow both listed; XVA view shows CVA and FVA per case |
| Responsiveness | Recorded measurements at 10,000 and 50,000 existing trades: first window, scroll, one CSA curve edit affecting 8,000 rows, Price freeze. Recorded, not gated |

## Out of scope

Real engines, CRIF, MQ and HPC dispatch, MVA and SIMM (next), individual-addition iteration (XV10),
the production database, authentication, AI.
