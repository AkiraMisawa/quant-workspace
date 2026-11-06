# ADR-0001: The client is React and TypeScript, with Mantine, ExGrid's React adapters, TanStack Query and Zustand

Status: **Accepted** (2026-10-26, owner's decisions). Rewritten the same day: the first version
recorded only "React with ExGrid" and left the component library, state ownership, dependency
consumption, touch ownership and rollout as open questions A1–A5. The owner's architecture update of
2026-10-26 answered A1 and A2; A3–A5 are now defaults or open items below.

## Context

The handoff of 2026-10-24 recorded C03: "Initial client is Blazor with MudBlazor and the ExGrid
family. React comparison is deferred." On 2026-10-26 the owner replaced it, first with "React, with
the in-development ExGrid for data grids, working on mobile, tablet, laptop and desktop", then with a
full frontend architecture update. The handoff's precedence rule makes the latest explicit owner
instruction win.

What ExGrid offers React is recorded in
[the frontend inventory](../reviews/2026-10-26-frontend-inventory.md).

## Decision

**CONFIRMED (owner, 2026-10-26):**

| Concern | Choice |
|---|---|
| Language and framework | React + TypeScript |
| Components, layout, theming, command interfaces | Mantine, themed into a distinct Quant Workbench visual system rather than default styling |
| Grids, spreadsheet interaction, pivots, supported charts | The ExGrid family through its React adapters (`@ex-grid/react`, `sheet-react`, `pivot-react`, `chart-react`, `board-react`) |
| Server state | TanStack Query |
| Client workspace state | Zustand, workspace-scoped; domain logic is not coupled to it ([ADR-0003](0003-client-state-and-the-effective-pricing-input.md)) |
| Backend application services | ASP.NET Core (.NET 10 per D01) |
| Agent to frontend | AG-UI |
| Form factors | Mobile, tablet, laptop, desktop; mobile accepted only after target-device verification (C04, UX02) |

AG Grid, MUI X or another grid library is never introduced as a permanent replacement for ExGrid,
and a homemade table is never presented as ExGrid. Jotai is an alternative to Zustand only if
representative benchmarks show a meaningful advantage.

This supersedes C03 and the MudBlazor recommendation of this ADR's first version. Text in the
handoff that names Blazor, MudBlazor, Razor pages, Fluxor or Blazor hosting modes is superseded where
it names client technology; the behaviour it describes is unchanged.

**DEFAULT (record a reason before changing):**

- **Consuming ExGrid.** Install `@ex-grid/*` from npm at one exact prerelease version for the whole
  family (the packages depend on each other at exactly their own version), and import the stylesheet
  `@ex-grid/core` ships (`@ex-grid/core/styles.css`). Record the version in `IMPLEMENTATION_STATUS.md`.
  Moving the version is a deliberate change.
  *(Changed 2026-11-04: ex-grid published its packages to npm as `0.2.0-beta.3`, with the stylesheet
  inside `@ex-grid/core`. Until then `@ex-grid/*` was unpublished, and this project packed tarballs and
  copied `ex-grid.min.css` from a pinned ex-grid commit into `vendor/ex-grid/`; both are removed.)*
- **Theming ExGrid.** Every grid is drawn by ExGrid's Mantine Wrapper, `@ex-grid/mantine`, at the
  family's version, through one component of this repository (`client/src/platform/grid/WorkbenchGrid.tsx`),
  with its stylesheet `@ex-grid/mantine/styles.css` loaded after Mantine's and the core's. The Wrapper
  maps the Mantine theme onto ExGrid's `--ex-` Visual Tokens in both schemes, draws the grid's menus,
  filter panel and editor with Mantine's components, and draws the cells in the font it has widths for
  (ex-grid ADR-0390 §9). This repository sets no `--ex-` token of its own.
  *(Changed 2026-11-06, ADR-0009, at the owner's request: until then there was no Mantine Wrapper
  (`@ex-grid/mui` was the only one planned), and `client/src/theme.css` mapped the theme onto the tokens
  by hand. That mapping also set `--ex-font-family` to the theme's font, whose widths the grid did not
  have, so a figure's `####` was judged for another face.)*
- **Touch.** ExGrid's TypeScript core has no touch input yet. Grid touch support is ex-grid's work;
  this project reports what it needs and does not patch around the grid.

## Open

| ID | Question | Gate |
|---|---|---|
| A5 | Does C18 (exposure inside the existing production host) still hold, with the React build served as static files behind developer-only routes? | Company integration (P6) |
