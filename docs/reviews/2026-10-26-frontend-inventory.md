# Frontend inventory, 2026-10-26

Inspection made for the owner's frontend architecture update. ExGrid observed at
`AkiraMisawa/ex-grid` `main` 79c0c70. Findings are classified as the update asked.

## 1. Confirmed existing implementation

### This repository

- No application code. The handoff documents, three JSON Schemas, synthetic examples, an illustrative
  SQLite schema and `tools/validate_handoff.py` (passes, including JSON Schema checks).
- No build, test or CI infrastructure yet.

### ExGrid's React surface

The TypeScript Script Core (`@ex-grid/core`) and thin React adapters exist in ex-grid's npm workspace
(`src/npm`). The workspace builds with Node 26.12.1 from ex-grid's flake (`npm ci && npm run build`
succeeded in this inspection); `npm pack` produces a tarball per package.

| Package | Exports | Notes |
|---|---|---|
| `@ex-grid/react` | `ExGrid`, `ExGridApi`, `templateColumn` | Hands the core its options on each render; rows are never React components |
| `@ex-grid/sheet-react` | `ExSheet`, `ExSheetApi` | Spreadsheet with ExSheet's engine in TypeScript |
| `@ex-grid/pivot-react` | `ExPivot` | Pivot view |
| `@ex-grid/chart-react` | `ExChart`, `ExSurface` | Charts and 3D surface |
| `@ex-grid/board-react` | `ExBoard` | Board Spec: a declarative layout of grids, pivots and charts (ex-grid ADR-0340) |

What the core's `GridOptions` offers that this project needs, all observed in its API report:

- **Data:** `rows` for in-memory data, or a `GridSource` with `rows(range, signal)`, `needRange`,
  `setSorts`, `setFilter`, `snapshot()` (with `rowSequenceVersion` and `totalCount`) and `subscribe`, so
  a server-held dataset can be paged without loading every row. `rowKey` gives stable row identity.
- **Editing without owning state:** `onEdit(intent)`, `onPaste(intent)`, `onClear`, `onUndo`, `onRedo`
  report intents; the Consumer applies them. Column `validate` returns an edit verdict; `cellMessage`
  shows a per-cell message; `onEditingChanged` reports whether an editor is open.
- **Commands and keys:** `declaredKeys` with `onDeclaredKey`, `contextCommands`, column `actions` with
  `onAction(row, column, action)`, `handKeyboardTo` / `returnKeyboard` for handing focus to and from
  application controls.
- **Presentation:** `density`, `rowHeight`, pinned columns, header groups, row marks, cell tones,
  selection summary, Find, column order/width/hidden events, `readView` / `showViewWith` for view state.
- **Copy safety:** `copyAnswer(Async)` and `copyCellCap`, so copy of a large range is answered or refused.

## 2. Missing or limited for this project

| Gap | Evidence | Consequence |
|---|---|---|
| No published packages | `@ex-grid/*` absent from the npm registry; versions are `0.0.0-dev` | Consume packed tarballs from a pinned commit (ADR-0001) |
| Stylesheets are not in the npm packages | ex-grid's React demo imports `src/ExGrid/wwwroot/ex-grid.min.css` and siblings from the NuGet projects | Copy them from the same pinned commit |
| No touch input in the TypeScript core | No pointer or touch handling in `src/npm/core/src`; ex-grid's Definition of Done marks responsive/touch "decided, not implemented", and its touch ADRs target the C# core | UX02 (iPhone) cannot pass with grids until ex-grid implements touch; narrow-screen layouts can still use detail panels |
| No Mantine wrapper | Only `@ex-grid/mui` is planned (ex-grid ADR-0350 §11) | Map the Mantine theme onto `--ex-` tokens here |
| Node engine range | ex-grid requires Node `>=22.23.4 <23`, `>=24.22.1 <25` or `>=26.12.1` | Build ex-grid through its flake; this repository should pin a compatible Node |
| API still moving | ex-grid is mid-migration (ADR-0350, ADR-0370); recent commits change the Script Core weekly | Pin a commit and re-run this project's grid tests when moving it |

## 3. Confirmed requirements (owner)

Recorded in [ADR-0001](../adr/0001-the-client-is-react-with-ex-grid.md),
[ADR-0002](../adr/0002-the-product-is-a-keyboard-first-workbench-and-the-generic-slice-comes-first.md) and
[ADR-0003](../adr/0003-client-state-and-the-effective-pricing-input.md).

## 4. Proposed architectural decisions

- Repository layout, contracts generation and the slice 01 design in
  [slice 01](../plans/slice-01-generic-workbench.md).
- The defaults recorded in ADR-0001 (ExGrid consumption, theming, touch ownership) and ADR-0003 §4.

## 5. Contradictions found and how they were resolved

| Contradiction | Resolution |
|---|---|
| C03 (Blazor + MudBlazor, React deferred) versus the owner's React instruction | Superseded by ADR-0001 |
| The update's section 10 (XVA editing first) versus its priority correction (generic slice first) | The later correction wins, consistent with the handoff's P1 (ADR-0002) |
| Effective values resolved by pure TypeScript in the client versus not loading every trade, with server-side validation of effective values | Open: ADR-0003 §5 |
| Handoff wording that names Blazor, Razor, Fluxor or hosting modes | Superseded where it names client technology; behaviour unchanged (ADR-0001) |

## 6. Unresolved issues

- ADR-0003 §5: where unloaded rows' effective input is materialised and validated. Gates the XVA slice.
- ADR-0001 A5: exposure inside the existing production host.
- Grid touch support in ex-grid. Gates UX02.
- Position Management: named, not specified.
- All handoff OPEN items in `docs/13-open-questions.md` remain open.

## 7. Performance constraints to measure, not assume

- Each React render hands the core every option; the core compares what changed. Stable `columns`
  arrays and callbacks are needed so an unrelated store change costs nothing (to be measured).
- In-memory `rows` at 10,000–50,000 rows versus a `GridSource`: measure memory, first render, scroll,
  bulk B/A and a main-CSA change separately, as ADR-0003 §4 lists.
