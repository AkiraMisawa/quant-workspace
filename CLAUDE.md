# Quant Workbench — implementation instructions

This repository handoff describes a composable quant application platform. It is not an implemented application. Read `START_HERE.md` and `docs/00-decisions.md` first.

## Product

The primary deliverable is capability AND UI composition: independent Capability, Workflow, View and Context models. Fixed XVA and live Rates pages are the first domain applications. SIMM comparison is one demonstration, not the platform's scope.

## Non-negotiable requirements

- Use React and the actual ExGrid family through its React adapters ([ADR-0001](docs/adr/0001-the-client-is-react-with-ex-grid.md), superseding the earlier Blazor/MudBlazor choice; where other documents still name Blazor as the client, ADR-0001 wins). Verify its pinned source/API; do not invent component parameters or substitute a homemade table and claim ExGrid integration.
- Keep the generic composition kernel independent of XVA, CSA, `IDataProvider`, QuantLib, MQ file formats and company policy.
- Only generic composition infrastructure is an OSS candidate. Domain/pricer modules and company integrations stay separately owned; use wholly synthetic examples.
- Fixed XVA submission uses the entire committed, validated typed workspace state at Price time. Never overwrite those values by reapplying defaults or fetching newer data on the server.
- Preserve immutable baseline and trade input versions separately from the editable workspace and the frozen submitted input. Provenance is explanatory, not a second calculation authority.
- Counterparty identity includes the registration/legal entity. Counterparty code alone is insufficient.
- Empty CVA recovery stays empty: analytics uses the applicable CDS recovery. Uncollateralized trade CSA is `nocsa_{ma id}`; its default discount curve is configurable (for example `jpy_ois`), with later manual edits preserved.
- Before and After are independent analytics requests. Individual-addition iteration is not cumulative.
- One public RunId identifies one business submission, including its multiple cases and internal calls. Do not create a Run per market tick.
- Every retained RunId must resolve its inputs, results, execution lineage and diagnostic artifacts, subject to authorization.
- Web/UI/core projects must not load native quant libraries. Use a separate persistent worker or an existing company service.
- Engine adapters and data sources must be replaceable without changing the composition core or UI. Never silently fall back from a real provider to a mock.
- Existing company bulk/HPC execution is reused; do not rebuild its dispatcher.
- AI calls application capabilities, not page navigation, Fluxor actions or grid automation. A view is optional for every workflow step.
- Dynamic views are validated declarative specifications rendered through approved components, never arbitrary generated HTML, C#, JavaScript or SQL.
- Keyboard, mouse, touch, responsive layouts and light/dark themes are acceptance requirements. Current component support must be verified on the target browsers.

## Decision handling

`CONFIRMED` means a user requirement. `DEFAULT` is an implementation proposal that may be used without repeated clarification; record a reason before changing it. `OPEN` needs discovery when its affected slice is reached. Do useful unblocked work first. Do not turn every open item into an up-front questionnaire.

This package supersedes older notes where they disagree about authoritative XVA input, ID hierarchy or initial client selection. See `docs/14-current-to-new.md`.

## Reading by task

- Scope and composition: `docs/01-product-scope.md`, `docs/02-composition-architecture.md`.
- Fixed XVA and state: `docs/03-xva-domain.md`, `docs/04-client-state.md`.
- Execution, IDs, storage and migration: `docs/05-execution-history.md`, `docs/06-persistence-migration.md`.
- Rates and company replacement: `docs/07-rates-live.md`, `docs/08-provider-boundaries.md`.
- AI and views: `docs/09-ai-and-views.md`.
- Wire contracts and examples: `docs/10-api-contracts.md`, `contracts/`, `examples/`.
- Acceptance and delivery: `docs/11-acceptance.md`, `docs/12-implementation-plan.md`.
- Questions and technical evidence: `docs/13-open-questions.md`, `references/sources.md`.

## Working rules

- Conversations with the owner may be Japanese. Use English for code, comments, repository documents and commit messages in this starter project.
- Start with the repository inventory and a runnable vertical slice. Avoid spending the first milestone building a large generic framework without a working demonstration.
- Prefer small pure functions for transitions, binding, validation and case expansion; thin endpoints call application services.
- Keep financial semantics in domain modules and compatibility checks. JSON shape matching does not prove economic compatibility.
- Treat all example numbers as synthetic and all illustrative names/IDs as proposed contracts.
- Use bounded parallelism, durable correlation and explicit execution states. An HTTP timeout does not prove a remote computation stopped.
- Add tests for concrete business, concurrency and integration risks; do not mirror implementation details with redundant tests.
- Record exact package/engine versions when selected. Keep company-only packages out of the standalone build.
- Do not claim a real model, mobile support, a working agent or performance targets are verified when only a mock or scripted demonstration was exercised.
- Do not publish or deploy anything merely because this handoff exists.

## Commands

This handoff has no `.sln`, web app or native worker yet. `python3 tools/validate_handoff.py` validates the documents' example bundle and illustrative database. JSON Schema validation is enabled when the optional `jsonschema` Python package is present.

After creating the solution, document actual commands in the repository README. Expected categories are `dotnet restore`, `dotnet build`, `dotnet test`, worker startup, and browser tests; do not report these as completed before implementation.

## Completion reporting

For each slice report what runs, the exact verification performed, which providers are real versus simulated, the next concrete gap and any necessary user decision. Keep these facts in `IMPLEMENTATION_STATUS.md` once implementation begins.
