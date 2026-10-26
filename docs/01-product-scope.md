# Product scope and first release

## User value

Users can execute familiar fixed workflows quickly, compose new analyses from existing capabilities, see results in interactive views, and follow every retained result back to its inputs and execution. A selected result or entity can launch another workflow without rebuilding a page.

## Scope matrix

| Area | Initial implementation | Explicit boundary |
|---|---|---|
| Composition | Versioned registry, typed DAG, context binding, optional views, retained intermediate refs, row actions, unsaved reuse | No arbitrary code execution or universal workflow language |
| Dynamic views | Form, selector, grid, chart, metrics, progress and details rendered by approved Blazor components | Spreadsheets/pivots supported through ExGrid adapters as verified; no fabricated APIs |
| XVA | Full fixed input flow, effective snapshot, B/A, individual additions, asynchronous deterministic provider, history and diagnostic bundle | Real company XVA requires company adapter and numerical checks |
| Rates | Actual native-worker-backed IRS pricing, then a defined XCCY product; live/manual/capture; synthetic markets | Mock prices do not count as real analytics completion |
| CRIF / IM | Independently callable capabilities, typed artifacts, before/after SIMM comparison | Mock results clearly labeled until a real engine is connected |
| AI | One real approved agent runtime, material-choice resolution, typed workflow and view proposals, shared executor | Scripted interaction is a development harness, not completion of real AI integration |
| History | Runs/cases/steps/artifacts and input/config inspection, partial failures, rerun from pinned refs | Quote management and trade booking are not initial scope |
| Usability | Keyboard/mouse/touch, responsive layout, light/dark, large-data handling | Mobile accepted only after target-device verification |
| Migration | Developer-only routes/API authorization, separate state, legacy read-only indexing | No compulsory bulk movement of old folders before the new UI ships |

## Three visible demos plus the composition proof

1. **Composition proof:** select synthetic entities, run a workflow with hidden intermediate data, reveal that data using a new View definition, execute a row action, rebind the unsaved workflow and Save/Pin it. Adding the view requires no new Razor page.
2. **XVA:** load two new trades, change main CSA, manually edit one row, commit the active editor with Price, run individual-addition cases, inspect results and recover the original engine artifacts using RunId.
3. **Rates:** enter swaps, start synthetic ticks, edit one row, observe only current-revision prices, stop live mode, calculate once and capture a selected result.
4. **AI:** request SIMM comparison for several counterparties; resolve an ambiguous MA/CSA; execute independently callable preparation and margin capabilities; show a comparison, then change its view without repricing.

## OSS versus domain ownership

Potential OSS packages contain capability contracts, workflow/view/context specifications, generic validation extension points, references, renderer adapters and generic execution abstractions. They contain no CSA/CVA logic, company entitlement rules, `IDataProvider` paths, approved model policies or proprietary SDK dependencies.

The personal application can contain synthetic domain examples in separately named sample modules. Company business applications are separate modules/packages. Publication is a separate decision; this handoff grants no publishing instruction.

## Deferred extensions

React client, package-based/cumulative iteration, shared-Before result caching, visual drag-and-drop workflow designer, external bond analytics integration, team publication and sales/trader quote lifecycle are extension points. The first release does not need their complete implementation.

## Success criteria

The platform must demonstrate a useful new workflow/view without changing a fixed pricer; the fixed pages must execute through the same services; the company provider replacement must not require UI/kernel edits. The first release is not complete when only polished mock screens exist.
