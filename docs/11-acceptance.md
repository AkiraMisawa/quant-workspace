# Acceptance matrix

These are implementation gates, not claims that application tests have already run. Numerical fixtures are synthetic unless a provider-specific real-engine fixture explicitly says otherwise.

## Composition and UI

| ID | Scenario | Observable requirement |
|---|---|---|
| CO01 | Add nonfinancial and Rates capabilities | Generic kernel compiles without domain references and requires no kernel edits. |
| CO02 | Headless versus two different views | Same accepted workflow produces equivalent referenced outputs; views do not own execution. |
| CO03 | Reveal hidden intermediate | Retained data appears without re-execution; expired data reports unavailable. |
| CO04 | Rebind unsaved workflow | Structure reused, context/permissions/CSA/data versions validated again. |
| CO05 | Row action from dynamic grid | Registered action builds a new validated workflow; no bespoke Razor page. |
| CO06 | Save and Pin | Navigation stores definition/version references and works without a new LLM call. |
| CO07 | Invalid definitions | Cycles, undeclared ports, forbidden component/action, incompatible semantics and code snippets are rejected. |

## Fixed XVA

| ID | Scenario | Observable requirement |
|---|---|---|
| XV01 | Same CP code, two legal entities | Correct registration drives MA/NS/CSA/trades; no cross-entity collision. |
| XV02 | Main CSA follow then manual row edit | Price uses the manually edited row's CSA/curve. |
| XV03 | Price while a cell editor is active | Valid edit commits before freezing; invalid edit blocks submission. |
| XV04 | Change context twice with reversed response order | Old response cannot overwrite the latest context or enable a mismatched Price. |
| XV05 | Pending preparation on Price | Calculation waits or is disabled; no mixture of source versions. |
| XV06 | Complete B/A selection under virtualization | Header selection affects its declared full scope, not just rendered rows. |
| XV07 | CDS versus CVA recovery | Changes are separate and map to the correct engine fields. |
| XV08 | Netting legality | Adapter encodes correct grouping; no cross-NS collision in bulk conversion. |
| XV09 | Reuse a draft trade | Same TradeInput revision is referenced by another Run; explicit refresh makes a new version. |
| XV10 | Individual-addition iteration | Each case adds exactly one candidate to the fixed configured background; no cumulative portfolio. |
| XV11 | Baseline stability | Price and Run completion do not refetch/overwrite Original or book New Trades. |
| XV12 | Server semantic preservation | Input units may be normalized; chosen CSA/curve/B/A and source versions are not replaced. |
| XV13 | Retry submit after connection loss | Same idempotency key/input returns the same Run; changed input with the key is rejected. |
| XV14 | Edit while old Run is executing | Old Run remains frozen; new draft revision and later Run are independent. |
| XV15 | Empty versus zero CVA recovery | Empty reaches analytics unchanged and invokes CDS-recovery fallback; zero is an explicit override. |
| XV16 | Uncollateralized default and override | CSA is `nocsa_{ma id}`; configured curve such as `jpy_ois` is displayed, and a later manual curve edit survives Price. |

## Execution and persistence

| ID | Scenario | Observable requirement |
|---|---|---|
| EX01 | Before/After and FVA/non-FVA/risk | Independent logical requests have explicit side/config/scenario bindings. |
| EX02 | CRIF dependency | Dependent work starts only with the required successful artifact revision. |
| EX03 | Duplicate/late MQ reply | Durable correlation maps it to the original attempt/request; duplicate completion has no duplicate business effect. |
| EX04 | Crash around dispatch | Restart recovers durable state; uncertain sends are reconciled rather than blindly repeated. |
| EX05 | Timeout/cancel | Unknown outcome and cancel requested remain distinct from failed/cancelled. |
| EX06 | One failed comparison case | Successful cases remain visible; failed deltas are unavailable, never zero. |
| EX07 | RunId forensic export | Bundle manifest includes available input/output ZIPs, config, lineage and checksums; controlled access. |
| EX08 | Artifact write/DB failure | No committed reference to incomplete bytes; staged/orphan bytes are recoverable or collectible. |
| EX09 | Legacy history | Original IDs and locations resolve read-only; absent user edit history is labeled unknown. |
| EX10 | Retention | Artifacts referenced by active workspaces/runs/retained views are protected. |
| EX11 | Existing company bulk service | Case/request bindings survive its partition/aggregate boundary; no new HPC dispatcher required. |
| EX12 | Physical storage move | Verified copy then locator cutover; one authoritative byte location and no broken retained references. |

## Rates and replacement

| ID | Scenario | Observable requirement |
|---|---|---|
| RT01 | Real IRS manual valuation | Native worker actually called; provider-specific numerical reference checked. |
| RT02 | XCCY supported scope | Declared product/conventions and requested fair quantity validated; actual engine result, not placeholder. |
| RT03 | Edit a row during slow calculation | Result for superseded input revision cannot overwrite current row. |
| RT04 | Fast tick stream with slower engine | Bounded pending work/coalescing; completed current-input frames can display monotonically without requiring the very latest tick. |
| RT05 | Stop and manual/capture | Subscription stops; manual price and capture have explicit fixed versions; no Run per tick. |
| RT06 | Complete frame capture | Persisted rows share their declared frame/input/market basis; mixed frames are not labeled coherent. |
| RT07 | User isolation | Subscription and cached/result access obey caller data rights. |
| PR01 | Fake versus real worker | Startup registration switch requires no UI/workflow/kernel changes. |
| PR02 | Company-only SDK absent | Personal/core build still succeeds. |
| PR03 | Unsupported provider option | Structured error; no silent omission or mock fallback. |
| PR04 | Engine changed | Version/provenance retained; numerical compatibility evaluated separately from interface compatibility. |

## AI and interaction

| ID | Scenario | Observable requirement |
|---|---|---|
| AI01 | Real agent SIMM request | Real runtime calls registered tools and displays returned data; mock analytics explicitly labeled. |
| AI02 | Ambiguous MA/CSA | Structured candidates/selection bound to the current plan revision. |
| AI03 | Independent CRIF or SIMM | Executes without loading/navigating the XVA page. |
| AI04 | Agent reconnect | Resumes state without accidentally creating another Run. |
| AI05 | Display-only follow-up | Sort/chart/detail uses retained results when available without repricing. |
| AI06 | Prompt requests arbitrary code/action | Definition/policy validators prevent an unregistered execution path. |
| UX01 | Keyboard-only journey | Search, choose, load, edit, B/A, Price, inspect results and return focus without mouse. |
| UX02 | iPhone touch journey | Actual target Safari/device: tap selection, edit, scrolling, action buttons and dialogs work. |
| UX03 | Responsive and themes | Narrow/wide layouts; readable grids, details, errors, focus and light/dark contrast. |

## Provisional performance workloads

Use 10,000 existing trade rows to inspect first useful render, scrolling, filtering, header selection and editing. Use 20 users × 50 live rows × 1 update/second as an initial Rates workload. These are DEFAULT test scenarios, not measured results, contractual limits or promised latency. Record hardware, worker/provider, memory, CPU, p50/p95 calculation and end-to-end age before agreeing a target. Do not use a fast fake as proof of real-engine capacity.

## Test strategy

Pure unit tests cover transitions, case construction and mapping. Application service tests cover preparation/submission with fake sources. Provider contract tests cover boundaries and errors. Real-engine numeric tests cover selected products. A small bUnit suite covers component integration; browser tests cover grid editing/focus/touch and end-to-end flows. Match the current repository's test tooling after inspecting it; avoid a giant Fluxor harness for every page.
