# Composition architecture

## Four independent models

| Model | Owns | Does not own |
|---|---|---|
| Capability | Versioned business operation; input/output ports; applicability, effects and validation hooks | A fixed Razor page or a chosen engine SDK |
| Workflow | Dependencies, bindings, parallel maps, conditions in the bounded supported language; final outputs | Mandatory rendering of each step |
| View | Layout, editors, grids/charts, selected data references, actions | Pricing rules or authority to bypass validation |
| Context | Selected entities, dates, data versions, identity and execution purpose | A mutable global singleton or untrusted AI-supplied entitlement |

An output may feed several steps and several views. Intermediate outputs need no view. A retained intermediate output may acquire a new view later. Changing sort, pivot or chart definitions does not execute pricing again; changing financial inputs does.

## Logical modules

| Module | Responsibilities |
|---|---|
| Composition.Contracts | Versioned descriptors, binding expressions, TypedDataRef, WorkflowDefinition, ViewDefinition |
| Composition.Application | Registry, composition validation, context resolution, action binding, run submission, policy extension points |
| Composition.Execution | Executor port, runtime adapter, durable lifecycle/event interfaces |
| Composition.Blazor | Allowlisted renderers, action dispatch, ExGrid integration |
| Quant.Domain.Xva / Rates / Margin | Financial contracts, semantics, case construction and fixed workflows |
| Quant.Application | Workspace preparation, accepted-input freezing, domain capability handlers |
| Quant.Adapters.* | Engine and source mappings, transport, raw artifact capture |
| Quant.Web | Thin HTTP/stream endpoints, authentication/policy integration and application composition root |
| Quant.NativeWorker | Native engine loading and computation outside the Web process |

Names are proposed. The generic modules must build without domain modules. Logical separation does not require a network call between every module. Inspect an existing repository before deciding project granularity.

## Capability descriptor

A registered descriptor declares capability ID/version, input/output contract identifiers, execution kind (`requestResponse`, `job`, or `subscription`), supported effects, domain validator identifier, permission policy identifier and compatible providers. The bounded JSON catalog in this package is a discovery projection: validator/policy/provider registrations remain server-owned registry metadata, outside that strict wire schema. Presentation hints are optional. Exact provider coverage is data-driven, not a claim that every provider supports every product.

Use coarse business operations. `margin.simm.calculate` and `pricing.xva.evaluate` are meaningful independently callable operations. Do not expose private CSV field rewriting or a page's Load button as the generic platform API.

## Typed data references and intermediate values

Use `TypedDataRef` to identify an immutable dataset/artifact revision. The bounded wire object contains a data ID, contract ID/version and immutable revision. Media kind, lineage, underlying artifact and resolution location are server-resolved metadata, not caller-selected URLs in the reference. A reference is not a bearer permission grant.

Actual large values live in the data/artifact store. Small scalar values may be embedded in typed outputs. Workflow bindings refer to an output port or a data reference, not to a formatted grid cell or arbitrary JavaScript expression.

Data lifetime rules:

- A running consumer keeps referenced inputs alive.
- Run retention covers retained execution inputs and outputs.
- Saved views/workflows that promise replay must reference retained data or declare that input rebinding is required.
- An ephemeral or expired intermediate result is reported as unavailable, never replaced silently by recomputation.
- A view can be disposed without disposing a running business operation or retained dataset.

## Composition validation

Validate registry membership, schema versions, DAG acyclicity, binding paths, required inputs, compatible output/input contracts and supported execution kinds. Domain validators then check economic meaning: scope, currency/unit, value date, market/model basis, CSA applicability and provider coverage.

An input can originate from a literal typed parameter, selected context, an earlier step output or controlled application policy. Policy fields are server-owned. The language initially permits references and registered transformations; arbitrary expressions and executable snippets are excluded.

Rebinding a template retains its structure but revalidates current permissions, entity choices, data versions and compatibility. It does not reuse an old authorization decision.

## Context-aware actions

An action has an ID, title, target entity contract, workflow template reference and parameter bindings. Grid selections provide entity references, not implicitly trusted business values. An action is available only when its contextual requirements can be satisfied. Invoking it uses the same application submission path as a form or AI request.

For example, a SIMM comparison row can open the retained CRIF dataset or launch a new comparison with a different CSA. The first operation creates a view; the second creates a new analysis submission after binding and validation.

## Lifecycle of definitions

Ephemeral workspace definition → personal saved version → pinned personal navigation entry. Team review/publication is deferred. Pinning stores navigation metadata and a validated Workflow+View reference; it does not generate Razor code. A saved workflow can execute without another LLM call.

## Execution substrate

Define a runtime adapter. A bounded reference DAG executor is sufficient for the first composition proof; durable analytics execution must persist its lifecycle and correlation before actual external dispatch. Evaluate existing supported runtimes, including previously discussed Elsa/Temporal candidates, against current company constraints before expanding custom execution infrastructure. Neither product is selected here.

Reuse the company's bulk executor/HPC partitioning. One capability invocation may produce many logical analytics requests or submit one supported bulk request with multiple case bindings. This is independent of whether the application renders one or many rows.

## Required proof of independence

Register a nonfinancial synthetic capability pair, then a Rates capability, without changing the generic kernel. Run the same workflow headlessly and with two View definitions. Reveal a retained intermediate result and invoke a row action. These tests are stronger evidence than a diagram of interfaces.
