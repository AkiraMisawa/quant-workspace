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

- **Consuming ExGrid.** `@ex-grid/*` is not published. Pin an ex-grid commit, build its npm
  workspace, and install the packed tarballs into this repository; copy the stylesheets the packages
  need (`ex-grid.min.css` and siblings, which live in ex-grid's NuGet projects, not in the npm
  packages) from the same commit. Record the commit in `IMPLEMENTATION_STATUS.md`. Moving the pin is
  a deliberate change.
- **Theming ExGrid.** There is no Mantine wrapper for ExGrid (`@ex-grid/mui` is planned for MUI only).
  Map the Mantine theme onto ExGrid's `--ex-` Visual Tokens in one module of this repository, owning
  no geometry and no DOM inside the grid's root, as ex-grid's ADR-0030 limits any wrapper.
- **Touch.** ExGrid's TypeScript core has no touch input yet. Grid touch support is ex-grid's work;
  this project reports what it needs and does not patch around the grid.

## Open

| ID | Question | Gate |
|---|---|---|
| A5 | Does C18 (exposure inside the existing production host) still hold, with the React build served as static files behind developer-only routes? | Company integration (P6) |
