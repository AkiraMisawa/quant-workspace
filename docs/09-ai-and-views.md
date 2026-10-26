# AI interaction and declarative views

## Roles

| Participant | Responsibility |
|---|---|
| Blazor AI workspace | Conversation, structured choices, editable inputs, progress and result views |
| UI-agent transport adapter | AG-UI-compatible event exchange and reconnect/turn identity |
| Agent runtime adapter | Model interaction, tool proposals and conversation state |
| Application tool gateway | Authenticated capability discovery, validation, binding and execution |
| Composition executor | Validated workflow execution and retained typed results |
| Domain adapters | Financial semantics and real/mock engine integration |

AG-UI is a UI-agent interaction protocol, not an LLM, model subscription, agent SDK or analytics transport [S03]. Writing the application with Claude Code does not select Claude as its runtime model. Copilot SDK was discussed but is not selected or assumed to support the company's unattended authentication. Establish one supported runtime/auth path in a focused spike; keep its adapter replaceable.

## SIMM demonstration

User: "Compare the incremental SIMM of adding Trade X to counterparties A, B and C."

1. Resolve each counterparty registration and value date through authorized capabilities.
2. Bind MA/NS and CSA. If there is more than one economically meaningful candidate, render a structured choice containing identifiers and key terms. A configured default can be proposed, but not silently treated as a known user choice in this ambiguous case.
3. Pin trade, baseline, market and SIMM methodology/configuration versions. Build an explicit analysis specification with one case per resolved context.
4. Prepare before/after portfolio inputs; generate CRIFs as required by the selected provider. CRIF generation is independently callable. Do not reuse new-trade CRIF across contexts unless its dependencies actually match.
5. Calculate SIMM for each required side. The LLM does not calculate the numbers.
6. Pair compatible results and compute `after - before`, retaining currency, methodology and lineage. Partial failure produces an unavailable delta, not zero.
7. Return typed references and render the comparison in ExGrid, optionally adding a chart.
8. "Show risk-class detail" binds a different view to retained data if it exists. "Use another CSA for B" creates a new resolved input and calculation.

The user may stop after independent CRIF generation or SIMM calculation. These operations must not require opening the XVA page.

## Fixed UI authority versus AI input construction

The fixed XVA page submits its final committed typed state. It is not reconstructed from an AI-style intent. AI may initially submit a high-level intent; the application resolves it into a complete, validated effective analysis input using pinned data and explicit choices. Both paths ultimately execute immutable accepted inputs.

Do not force the AI through Fluxor effects, DOM clicks or fixed-page preparation commands. Share the underlying context-resolution, validation and execution services, not UI orchestration.

## Views

`ViewDefinition` contains versioned component kinds, data bindings, visible fields, formatting, layout and action IDs. Initial kinds: `form`, `choice`, `grid`, `chart`, `metric`, `progress`, `details`. Add spreadsheet/pivot adapters after verifying actual ExGrid APIs. Component adapters interpret typed specifications; AI-generated source code is not rendered.

View validation checks component/field/action allowlists, binding existence, compatible data types, formatting bounds and authorization to resolve references. Row-level actions are explicit registered workflows/actions with typed parameter binding. Raw SQL, scripts, remote executable URLs and arbitrary C# expressions are outside the specification.

Sorting, filtering and pivoting may operate against an authorized server-side dataset query when the result is large. They must not load every cell into an LLM prompt. Include units/currency/date in column metadata; never infer them from a column title alone.

## Intermediate data

A step can return no visible UI and still create a retained `TypedDataRef`. Multiple views may bind the same reference. A new view can be attached after execution without changing the Workflow definition. If required detail was not calculated or has expired, explain that and request the corresponding new calculation; do not fabricate it.

Workflow, view and live subscription lifetimes are independent. Closing a view detaches its rendering/subscription; it does not erase a retained dataset or cancel a durable run unless the user requests cancellation.

## State, retries and user choices

Keep ConversationId/TurnId distinct from WorkspaceId and RunId. Bind choice prompts to a plan/input revision; discard or revalidate answers to superseded revisions. Reconnect should retrieve current run state and replay/deduplicate transport events, not resubmit calculations. Tool calls carry idempotency keys through the same application executor as fixed UI requests.

Show concise actions, selected assumptions and execution progress. Do not expose private model reasoning. Operational event text and artifacts are evidence; a fluent generated explanation is not evidence that computation succeeded.

## Execution policy

Initial scope is read-only analytics. Server-side policy governs capability availability, data access, provider selection and resource limits. Agent-supplied identity/permissions are ignored. Booking, position mutation and external quoting are separate future capabilities with their own rules. Do not insert generic approval dialogs for already resolved ordinary analytics; prompt for material ambiguity or a policy-required action only.

## Acceptance

The real-AI milestone requires a real runtime producing a typed proposal, resolving a CSA choice, executing registered capabilities and rendering actual returned results. A scripted transcript can test UI deterministically but must remain labeled as such. See AI01-AI06 in `11-acceptance.md`.
