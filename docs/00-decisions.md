# Decision register

Status: **CONFIRMED** = explicit user requirement or supplied operational fact. **DEFAULT** = recommended initial choice, not a claim of user approval. **OPEN** = discover or decide before the affected implementation. **DEFERRED** = extension boundary only, outside the initial implementation.

## Confirmed requirements

| ID | Decision / requirement |
|---|---|
| C01 | The product centers on general-purpose Capability composition and independent UI composition. XVA, Rates and SIMM are applications/examples. |
| C02 | Capability, Workflow, View and Context are distinct models. A workflow may be headless; intermediate results can be hidden and shown later. |
| C03 | ~~Initial client is Blazor with MudBlazor and the ExGrid family. React comparison is deferred.~~ Superseded 2026-10-26 by [ADR-0001](adr/0001-the-client-is-react-with-ex-grid.md): the client is React, its grids are the ExGrid family through its React adapters, and it targets mobile, tablet, laptop and desktop. |
| C04 | Expert keyboard operation, mouse, touch, responsive layouts, light and dark themes are required. |
| C05 | Fixed XVA calculates the final committed and validated typed UI state, including manual edits made after automatic following. |
| C06 | Baselines are immutable versions. Value date, counterparty registration and main netting set changes reload dependent data. |
| C07 | A counterparty registration distinguishes legal entities even when counterparty codes match. |
| C08 | CDS recovery and CVA recovery override are separate. CDS term structures and proxy Region/Sector/Rating are editable inputs. |
| C09 | MA/NS and CSA grids have main selections; multiple CSAs are possible. Trade rows have independent B/A membership and per-row CSA/discount-curve selection. |
| C10 | Before and After are independently sent to analytics. Multiple configs, scenarios, preparation steps and risk requests can belong to one business submission. |
| C11 | Initial iterative XVA is individual addition, not cumulative. Package grouping is a later extension. |
| C12 | CRIF generation and IM/SIMM calculation can be invoked independently. CRIF generation alone is not IM calculation. |
| C13 | Workspace reuse must preserve loaded draft trades across repeated submissions; a completed run does not book them or replace the original portfolio. |
| C14 | Developer diagnostics must be retrievable from RunId, including associated input/output ZIPs where retained. |
| C15 | Rates page accepts one-line IRS and cross-currency swaps; live PV/rate updates, manual pricing and a synthetic market feed are required. |
| C16 | Web server project must not depend directly on native C++ analytics. Company engines and data sources must be replaceable. |
| C17 | Initial population is approximately 20 users; no measured throughput or latency promise has been made. |
| C18 | Production remains usable while new pages are exposed to developers within the existing hosting arrangement where feasible. |
| C19 | Existing company execution service already partitions multi-counterparty/multi-NS providers into HPC work and aggregates outputs. Reuse it. |
| C20 | Only generic composition infrastructure is an OSS candidate. XVA/pricer domain logic, company policy and proprietary adapters remain separate. |
| C21 | AI uses shared capabilities without running the fixed UI flow. Material ambiguity such as MA/CSA selection requires explicit resolution. |
| C22 | Unsaved workflows can be rebound and reused; saving/publishing is not required before a repeat. Data-dependent validation repeats on rebind. |
| C23 | An empty CVA recovery override is passed as empty; analytics automatically uses the applicable CDS recovery. No additional effective-recovery scalar is required. Zero is an explicit override. |
| C24 | Uncollateralized trade CSA is `nocsa_{ma id}`. The default uncollateralized discount curve is configurable (for example `jpy_ois`); later user edits remain authoritative. |
| C25 | (2026-10-26) Frontend: React + TypeScript, Mantine, ExGrid's React adapters, TanStack Query, Zustand; ASP.NET Core backend; AG-UI. No other grid library replaces ExGrid. [ADR-0001](adr/0001-the-client-is-react-with-ex-grid.md) |
| C26 | (2026-10-26) The product is Quant Workbench, a keyboard-first, AI-native composable platform; XVA, Rates, IM/SIMM and Position Management are domain applications. The generic workbench slice comes first. [ADR-0002](adr/0002-the-product-is-a-keyboard-first-workbench-and-the-generic-slice-comes-first.md) |
| C27 | (2026-10-26) Primary acceptance criterion: a new capability is exposed through commands, AI, user-defined workflows and composed views without modifying the XVA pricer or adding a bespoke page. |
| C28 | (2026-10-26) Client state is split into server, workspace, local UI and execution state; financial dependency resolution is pure TypeScript decoupled from Zustand; ExGrid owns no business rule. [ADR-0003](adr/0003-client-state-and-the-effective-pricing-input.md) |
| C29 | (2026-10-26) The workspace stays responsive with tens of thousands of trades. The client holds the original data and the user's changes, resolves every row's effective values, and renders only what is in view; the backend validates the submitted values without re-deriving them (ADR-0003 §4). |

## Recommended initial choices

| ID | Default and rationale |
|---|---|
| D01 | Target .NET 10; pin an available approved patch and compatible dependencies at bootstrap. |
| D02 | Start with a modular application host plus a separate persistent native worker. Logical modules do not each imply a deployed service. |
| D03 | Use an editable Workspace distinct from ephemeral UiSession. AnalysisRun/public RunId represents one business submission, with multiple Cases underneath. |
| D04 | Local demo: SQLite for structured metadata and an immutable directory artifact store for bytes. Production relational DB remains open. |
| D05 | One typed workspace state is the source for UI projections and final submission; immutable baseline and edit provenance remain separate. |
| D06 | Manual field edits own that field until reset to automatic; a later main change updates only compatible automatic fields. This is a proposed policy, not a confirmed historic rule. |
| D07 | On incompatible context changes, preserve downloaded new trades in a staging area, reset source-specific assignments and require revalidation before reuse. |
| D08 | Commit one workspace revision atomically, wait for relevant preparation, validate, freeze input, then create the Run. Any failed edit prevents submission. |
| D09 | Generic runtime executes a bounded validated DAG behind an executor interface. Evaluate an existing workflow runtime before expanding into a general durability engine. |
| D10 | AG-UI is the preferred UI-agent transport direction. Agent SDK/model/authentication remain replaceable and unselected. |
| D11 | Use JSON Schema plus domain semantic validators; schema validation alone never authorizes or economically validates a request. |
| D12 | Personal Save/Pin of validated Workflow+View definitions follows the first composition slice; team publishing/quoting governance is later. |
| D13 | Shared-Before calculation reuse is off initially. Bulk provider submission is allowed and is a different concern. |
| D14 | Use English repository material and a Japanese onboarding overview. |

## Superseded earlier ideas

| Earlier idea | Current instruction |
|---|---|
| Fixed XVA sends only overrides; server refetches baseline and reconstructs the effective state. | Send or commit a complete fixed effective input revision. Server validates and translates it without changing its meaning. |
| Re-fetch Original on Price or overwrite it after pricing. | Keep immutable baseline versions. A new explicit context refresh creates a new version. |
| One AI comparison is always a group of unrelated public Runs. | Default: one AnalysisRun, many Cases; optional AnalysisGroup spans later separate submissions. |
| Draft inputs live only under a Run folder. | TradeInput exists before the Run and is referenced by Workspace and immutable case input. |
| Every tick creates a Run. | LiveSubscription owns ephemeral updates; explicit capture creates a durable run/snapshot. |
| The new platform must implement company HPC partitioning. | The company execution provider already does this; the adapter preserves target identity and provenance. |
| Generic core includes XVA input file paths and scenario grammar. | Domain modules own these; generic composition uses typed contracts and references. |
| Earlier draft required a pinned `effectiveCvaRecovery` when override was empty. | Superseded by C23: empty invokes the analytics' documented CDS-recovery fallback. |
| Earlier draft represented uncollateralized trade CSA as null. | Superseded by C24: store `nocsa_{ma id}` with explicit uncollateralized semantics. |

## Scope that remains open

Actual ExGrid version/API/browser coverage, Blazor hosting mode, quant worker implementation, exact XCCY product conventions, agent runtime and unattended auth, company SDK/API contracts, per-field Before/After edit scope, later-main-change override behavior, production DB and retention are not silently settled by this document. See `13-open-questions.md` for nonblocking defaults and decision gates.
