# Implementation status

Last updated 2026-10-26. Commands are in [README.md](README.md).

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

- Storage is in memory. Runs and retained data are lost when the host restarts, and nothing is durable yet.
- There is no AG-UI or AI path yet. No authentication.
- Palette focus is handled by the shell itself: opening the palette and choosing a command let go of the focused element first. Keys typed before the next surface has the keyboard are dropped, never delivered to the grid behind or to a closing palette.

## Next

1. Slice 01b: Save and Pin (CO06), and the browser parts of CO03 and CO04.
2. The XVA workspace slice ([ADR-0003](docs/adr/0003-client-state-and-the-effective-pricing-input.md)):
   - a server-authoritative workspace with an edit log;
   - effective rows paged into ExGrid through `GridSource`;
   - measurements at 10k and 50k trades.
   It is waiting on the owner's confirmation of how changes are stored (ADR-0003 §6).
3. Rates, IM/SIMM, AI over AG-UI, and run history.
