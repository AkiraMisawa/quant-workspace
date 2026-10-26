# Slice 01: the generic workbench

Status: plan, 2026-10-26. Implements the first slice of
[ADR-0002](../adr/0002-the-product-is-a-keyboard-first-workbench-and-the-generic-slice-comes-first.md).
Nothing in it is financial: its capabilities are synthetic and non-financial, so no financial
convention is assumed.

## Goal

Prove, end to end and in a browser, that a capability registered once is reachable through the
command palette, a typed workflow, a declarative ExGrid view and a row action, and that the same
workflow can be reused with another context without saving it.

## What the user sees

1. `Ctrl+K` (or `/`) opens the command palette. Every action of the slice is a command in it: run a
   workflow, open a view of a result, rebind the context, focus a panel, switch theme.
2. A context bar shows the active context (here: a synthetic region and an as-of date) with the
   requested and the prepared context told apart.
3. Running "Station summary" executes a workflow of three synthetic capabilities on the server and
   shows its final output in an ExGrid view. The intermediate dataset (per-station readings) is retained
   but not shown.
4. A row action on the summary ("Show readings") opens a second, declarative view of the retained
   intermediate dataset, without executing anything again.
5. A second row action ("Summarise this station only") builds a new workflow submission from the
   selected row's entity reference, validated like any other.
6. Changing the region in the context bar and running the same unsaved workflow again rebinds and
   revalidates it, producing a new Run while the previous Run's results stay unchanged.
7. Every step works by keyboard alone, and with a mouse; light and dark themes both work.

## Capabilities (synthetic, non-financial)

| Capability | Input | Output | Purpose |
|---|---|---|---|
| `sample.stations.list` | context `region` | dataset of stations (`stationId`, `name`, `region`) | Context binding |
| `sample.readings.generate` | one station, `asOf`, `days` | dataset of readings, seeded and deterministic | `forEach` map; hidden intermediate |
| `sample.readings.summarise` | readings collection | dataset of per-station count, min, max, mean | Final output |

Readings are generated deterministically from a seed derived from the inputs and labelled synthetic in
the result's provenance.

## Architecture

```
client/  React + TypeScript (Vite)
  src/platform/commands/     command registry + Mantine Spotlight palette; one command path for keys, buttons, palette
  src/platform/catalog/      TanStack Query hooks over the capability catalog
  src/platform/context/      context store (Zustand) + coordinator (requested vs prepared)
  src/platform/workflows/    unsaved workflow drafts, submission, run polling
  src/platform/views/        ViewDefinition validator and renderer registry (grid, details, progress)
  src/platform/grid/         ExGrid adapter: stable row/column IDs, edit intents to commands, Mantine-to-token theme
  src/samples/               nothing domain-specific; the slice needs no client code per capability
server/  ASP.NET Core (.NET 10)
  Workbench.Composition.Contracts     descriptors, TypedDataRef, Workflow/View definitions, Outcome
  Workbench.Composition.Application   registry, composition validator, binding, reference DAG executor, run store port
  Workbench.Samples.Synthetic         the three capabilities above (no reference from the kernel to it)
  Workbench.Web                       thin endpoints: catalog, runs, data rows, definitions
vendor/ex-grid/  packed @ex-grid tarballs + stylesheets + PIN (commit)
```

- The wire vocabulary is `contracts/composition.schema.json`. TypeScript types are generated from it;
  C# records mirror it and a test validates the examples against both.
- Run storage is an interface with an in-memory implementation in this slice. SQLite and the artifact
  store (D04) arrive with the history slice; nothing here claims durability.
- The executor is the bounded reference DAG executor of D09 behind a port.

## Acceptance (from `docs/11-acceptance.md`)

| ID | Check | Test layer |
|---|---|---|
| CO01 | Kernel projects have no reference to the samples project; adding the sample module changes no kernel file | Architecture test (xUnit) |
| CO02 | The same workflow run headless and with two views yields the same output references | Application test |
| CO03 | Revealing the intermediate view does not execute; an expired reference reports unavailable | Application test + Playwright |
| CO04 | Rebinding an unsaved workflow revalidates and creates a new Run; the old Run is unchanged | Application test + Playwright |
| CO05 | A row action builds a validated workflow; no capability-specific page | Playwright |
| CO07 | Cycles, undeclared ports, unknown components/actions and code-like bindings are rejected | xUnit + Vitest |
| UX01 (slice part) | Palette, run, open intermediate view, row action and rebind by keyboard only | Playwright |
| UX03 (slice part) | Narrow and wide layouts; light and dark | Playwright |

CO06 (Save and Pin) follows in slice 01b. UX02 waits for ExGrid touch support.

## Out of scope

XVA, Rates, AI runtime, durable history, authentication. AG-UI arrives with the AI slice; the command
and capability boundaries built here are what it will call.
