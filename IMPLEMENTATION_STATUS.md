# Implementation status

Last updated 2026-10-27. Commands are in [README.md](README.md).

## Slice 01: the generic workbench

Plan: [docs/plans/slice-01-generic-workbench.md](docs/plans/slice-01-generic-workbench.md).

### What runs

- **Server** (`server/`): the composition kernel.
  - A capability registry, plus strict JSON contracts that refuse unknown members and code-like bindings.
  - Workflow and view validation.
  - A bounded DAG executor with `forEach` maps. Failed items are kept as unavailable, never zero.
  - Runs with idempotency keys, retained typed data references, and registered row actions (`openView`, `runWorkflow`).
  - HTTP API: catalog, runs, data read, action invoke, and definition validation.
- **Client** (`client/`):
  - A command palette (Ctrl/⌘+K). Its commands come from the catalog, so a registered workflow appears without a client change.
  - A context bar that keeps the last valid context in force.
  - A run dialog generated from the input contract.
  - Panels per Run or per opened view.
  - Declarative views rendered through ExGrid, with row actions in an ExGrid Action Column.
  - Light and dark themes.
  - Keyboard shortcuts for focus grid, next/previous panel, close panel and theme.
- **Sample module**: four synthetic capabilities (stations, readings, summary). They are non-financial on purpose, to show that the kernel knows nothing of XVA.

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Workbench.slnx` | 26 passed |
| `npm test` (Vitest) | 16 passed |
| `npx playwright test --repeat-each=3` (Chromium, headless, Linux) | 15 passed (5 tests × 3) |

The Vitest suite covers forms against contracts, view binding, command ranking and refusal, the workspace store, and palette commands built from the catalog.

| Acceptance | Status |
|---|---|
| CO01 | Passed (xUnit: a module added without kernel change; Vitest: its workflow gets a palette command) |
| CO02 | Passed (xUnit) |
| CO03 | Application part passed (xUnit). Browser part not yet: no Playwright test reads an expired reference. |
| CO04 | Application part passed (xUnit). Browser part partial: a context rebound by keyboard reaches the next Run, but the earlier Run is not yet checked as unchanged in the browser. |
| CO05 | Passed (Playwright: Show readings opens a view over retained data; Summarise this station runs a workflow) |
| CO07 | Passed (xUnit) |
| UX01 (slice part) | Passed in Chromium |
| UX03 (slice part) | Passed: dark and light themes; no sideways scroll at 390 px |

Not verified: Edge and Firefox, Windows and macOS, touch (UX02), a real phone or tablet, and performance.

### Providers

Every provider is simulated. All data is synthetic and labelled as such on screen ("synthetic data", "simulated"). No real model, market data source or company service is connected.

### ExGrid

- Pinned to ex-grid commit `3bf523dbb1bdaf747a6632c2c80d8f080a03f468` (moved from `79c0c70` on 2026-10-27 for the fetching source's `notifyChanged`) (`vendor/ex-grid/PIN`), through `@ex-grid/react`.
- A key sent to a newly drawn grid before it carries its Settled Mark is lost, by ExGrid's own contract. Browser tests wait for the mark, as ex-grid's own tests do.
- ExGrid's TypeScript core has no touch support yet.

### Known limits

- Storage was in memory in slice 01; slice 02 moved Runs, retained data and workspaces to SQLite.
- There is no AG-UI or AI path yet. No authentication.
- Palette focus is handled by the shell itself: opening the palette and choosing a command let go of the focused element first. Keys typed before the next surface has the keyboard are dropped, never delivered to the grid behind or to a closing palette.

## Slice 02: the XVA workspace and the Pricing DB

Plan: [docs/plans/slice-02-xva-workspace.md](docs/plans/slice-02-xva-workspace.md). Decisions: ADR-0003 §6 (resolved), §7 (built as proposed, still OPEN for the owner's review), C30 to C36 in `docs/00-decisions.md`.

### What runs

- **Generic workspace kernel** (`Workbench.Composition.Application/Workspaces`): an append-only edit log, overrides with their base, the carry check (carried, conflicted, lost), conflict groups, `DropScope` with the confirmed count, an atomic counterparty change (`ChangeScope`), and `Freeze` refused while a priced entity holds an unresolved edit. Every command names the revision it was taken against.
- **SQLite** (`Workbench.Persistence.Sqlite`, Microsoft.Data.Sqlite 10.0.14 with SQLitePCLRaw 2.1.13): one file holds generic tables for every domain: `Workspace` (current state), `EditLog` (append-only, `domain_kind` per entry), `AnalysisRun`, `SubmissionKey` and `RetainedData`. A workspace is restored after a restart from its source descriptor, and a descriptor that now acquires another version is refused. Deleting a workspace keeps its log. A Run left running by a stopped process is recorded as failed with code `Interrupted`.
- **Runs** record their owner, origin (`pricer`, `ai`, `workflow`, `action`), explicit relations (workspace and revision, parent Run, analysis group, conversation) and the result contracts their workflow declares. Idempotency keys are per owner and survive a restart.
- **Synthetic XVA module** (`Workbench.Samples.Xva`): three registrations (two share code ABC under different legal entities), agreements with CSAs and one uncollateralised (`nocsa_MA-003`), up to 100,000 generated existing trades per counterparty (matured ones are absent at a later date, and the last fixing moves with the date), three draft trades that belong to no counterparty, an empty CVA recovery on LE-01. The rules: a new trade takes the main CSA, a curve follows its CSA (including a CSA curve edit) unless the user set it, an uncollateralised trade starts on the profile curve, an ineligible CSA is an issue on the row and Price refuses it. A simulated CVA/FVA provider prices Before and After independently and falls back to the CDS recovery when the CVA recovery is empty.
- **HTTP**: `/api/xva/...` creates a workspace, pages effective rows (sorted on the server, Blank last as ExGrid's ADR-0023), edits, resolves held edits, moves the value date, previews and confirms a counterparty change, prices and deletes. `/api/history/...` serves the Pricing DB.
- **Pricing DB**: every Run newest first; a module registers a history projection; the XVA view adds value date, registration, trade counts, CVA and FVA Before, After and incremental. An unknown domain is refused.
- **Client**: the XVA workspace page (ExGrid over a fetching source with a Row Key; user-set, held and invalid cells painted as Cell States with their reasons; a counterparty change that shows what it drops by edit kind and asks first; Price as a button and the `Price XVA workspace` command) and the Pricing DB page (one domain at a time, `Open` opens a Run). `New XVA workspace` (Ctrl+Alt+N), `Open Pricing DB` (Ctrl+Alt+H) and `Price XVA workspace` (Ctrl+Alt+P) are palette commands.

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Workbench.slnx` | 58 passed |
| `npm test` (Vitest) | 21 passed |
| `npx playwright test` (Chromium, headless, Linux) | 7 passed; the two new XVA tests also with `--repeat-each=3` (6 passed) |

| Acceptance | Status |
|---|---|
| XV01, XV15, XV16, C24 | Passed (xUnit; Playwright shows the main CSA and the following curve) |
| Before and After independent (CLAUDE.md) | Passed (xUnit) |
| ADR-0003 §6: saved per edit, frozen at Price, survives a restart | Passed (xUnit on SQLite). The browser reload case is not tested yet. |
| ADR-0003 §6: counterparty change asks before dropping | Passed (xUnit and Playwright) |
| ADR-0003 §7: carried, lost, Price refused while unresolved | Passed (xUnit). A conflict cannot arise in the synthetic XVA data yet, since no editable field changes between value dates; it is covered on the kernel only. |
| Pricing DB | Passed (xUnit; Playwright: a Price lands in the list with origin `pricer` and in the XVA view with its figures) |
| EX06, a failed case shown unavailable in the browser | Not yet |

### Measurements

Release build on this Linux container, one request at a time with `curl`, at portfolio sizes of 10,000 and 50,000 requested existing trades (7,000 and 35,137 alive on 2026-10-26). Recorded, not gated; no target is agreed.

| Step | 10,000 | 50,000 |
|---|---|---|
| Create the workspace | 199 ms | 317 ms |
| First page of 120 rows, unsorted | 26 ms | 2 ms |
| A page in the middle under a new sort (first sort of that order) | 44 ms | 133 ms |
| Edit one trade | 24 ms | 11 ms |
| Edit a CSA's curve, reaching every trade on it | 46 ms | 149 ms |
| Move the value date (re-acquire and carry) | 114 ms | 336 ms |
| Price accepted (freeze, build and store the accepted input) | 295 ms | 963 ms |

Rows without an edit of their own are computed once per source version and set of CSA curve edits; a sorted order is kept per state. Scrolling in the browser at these sizes was not measured.

### Providers

Every provider is simulated and every figure synthetic, labelled `simulated` in the result and the Pricing DB.

### Known limits

- The trades grid does not filter: a filter set from a column header is refused with a message, not ignored. Paste is not wired.
- Requests act as one configured demo user (`Workbench:DemoUser`) until ME-ID sign-in (C30); roles (C31) are not enforced, so a user sees only their own Runs.
- Run views are matched to Runs by output name, not by contract.
- Touch waits for ExGrid's touch release (C36).

## Slice 03: the Rates line pricer

Plan: [docs/plans/slice-03-rates-line-pricer.md](docs/plans/slice-03-rates-line-pricer.md). Decisions: C37 to C43 in `docs/00-decisions.md`, ADR-0004 (accepted 2026-10-27).

### What runs

- **Kernel (ADR-0004)**: entities the user creates (`Create`, `Remove`), sources a module derives from the workspace (`IEditRules.Derive`, one scope per line), a Derive log entry with its carry summary whenever a derived source moves, and derivation again on load.
- **Rates module** (`Workbench.Samples.Rates`): a line's flat fields (C40, C43) read into typed terms; a resolver that refuses what it cannot honour on the row; a standard schedule generator; a schedule per line as a derived source whose periods are identified by leg and unadjusted dates, so cashflow edits carry, conflict or are lost when the terms move.
- **Pricing**: `IRatesPricingProvider` with the fake provider (`fake-rates/1`, every figure `simulated`): PV, the par rate or fair spread of the leg left without a quote, and 1bp pillar deltas when the row asks for risk, standing in for the engine's AD sensitivities (C38). A seeded synthetic market whose snapshot n is a function of its configuration.
- **Price** (`rates.price`): freezes the revision the user saw, prices its priceable lines against the snapshot the request names (an unknown one is refused), lists every other line as `notPriced` with its reasons, and refuses when nothing is priceable. One Run per Price.
- **Live**: subscriptions price the newest market with at most one calculation in flight and the newest update waiting; an accepted edit moves a subscription to its revision; a frame names its subscription revision, input revision and snapshot. No Run per tick. Frames stream as server-sent events. A workspace has one subscription on the server; pages attach to it (a new page first hears the frame shown now) and detach when they go. Once nobody has watched it for `Rates:LiveIdleStopSeconds` (300 by default), the next tick stops it and the workspace records why; a page opening on it then shows the last frame and offers Resume, which starts a new subscription on the current revision (owner, 2026-10-27).
- **Capture** (`rates.capture`): saves the frame the user saw as a Run, with its own snapshot, refused (`FrameMoved`) when another frame is shown by then.
- **HTTP**: `/api/rates/market`, `/api/rates/workspaces/...` (create, view, add, edit, remove a line, cashflows and cashflow edits, resolutions, valuation date, price, start live, delete), `/api/rates/live/{id}/frames|capture`.
- **Pricing DB**: the Rates view lists one row per Run: valuation date, price or capture, snapshot, lines priced, failed and not priced, execution mode.
- **Client**: the Rates page (`New Rates workspace`, Ctrl+Alt+R). One ExGrid row per line, keyed by line, with Header Groups over Result, Trade, Leg 1, Leg 2 and Terms; the line and its actions (Cashflows, Remove) are pinned. Cells the user set, held edits, lines that cannot be priced and figures of an older revision are painted as Cell States with their reasons. Each leg's second level (C43) opens and closes from a button beside the grid, and the group's caption says which. Cashflows opens a second ExGrid over the line's periods, whose dates, notional and rate are editable, with its notional exchanges below. Start live and Stop live, Capture, and Price (against the snapshot shown) are buttons and the palette commands `Price Rates workspace`, `Start or stop Rates live` and `Capture Rates live frame`. The edit queue the XVA page used is now generic and shared.
- **Configuration**: convention profiles and the synthetic market are configuration (`Rates` section). None is configured by default. `appsettings.Development.json` carries the convention table proposed on 2026-10-27, still unconfirmed, on a weekends-only calendar: its profile version says `unconfirmed` and every row names its profiles.

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Workbench.slnx` | 120 passed; the Rates tests (56) three times |
| `dotnet run` (Development) | Started; `/api/rates/market` advanced with the ticker |
| `npm test` (Vitest) | 27 passed |
| `npx playwright test` (Chromium, headless, Linux) | 9 passed; the two Rates tests also with `--repeat-each=3` (6 passed) |

| Acceptance | Status |
|---|---|
| A line typed by keyboard prices live, captures and prices once (C40, docs/07 §3) | Passed (Playwright) |
| Manual and live agree on the same input and market (docs/07 acceptance 4) | Passed (xUnit) |
| An edit moves the live subscription to its revision; capture refuses a frame no longer shown (spine 6) | Passed (xUnit) |
| Cashflow edits carry, conflict or are lost when terms move (ADR-0004) | Passed (xUnit). Not yet driven in the browser. |
| Touch, tablet and phone layouts | Not verified; waits for ExGrid's touch release (C36) |

### Providers

The provider and the market are simulated. Real holiday calendars, the confirmed convention table and a real worker are not wired.

### Known limits

- The convention table, the risk factors to report against and the meaning of "FX Forward (pts)" are open with the owner.
- The Pricing DB lists Rates Runs, not a row per line.
- Snapshots are kept for the life of the process.
- A collapsed leg whose hidden columns hold a value the user set says so in its caption (`Leg 1 ▸ ●`); the values are priced whether shown or not.
- The leg header itself does not expand its second level: that needs an ExGrid change (spec of 2026-10-27), so the toggle sits outside the grid, and expanding clears the grid's selection (ExGrid ADR-0011).
- A Price result opens as a Run; it does not fill the line grid's figures, which only live frames do.
- Per-line risk (the deltas) is in the Run's Risk grid only; the line grid shows PV and the solved quote.

## Next

1. The owner's review of ADR-0003 §7 (scopes, carry and conflict), then a synthetic source change that can produce a conflict, and the browser cases still open above (reload, failed case unavailable).
2. MVA with SIMM (`docs/12-implementation-plan.md` P2), then Rates.
3. ME-ID sign-in through MSAL (C30) and the visibility rules (C31, C32); AI over AG-UI with the company's Copilot behind the adapter port (C34).
4. Slice 01b: Save and Pin (CO06), and the browser parts of CO03 and CO04.
