# Sources and evidence

Checked 2026-10-24. Product requirements come from the owner's design conversation; technical links support the mechanisms, not the claim that this application has already been built.

## Product evidence

- Current conversation: fixed XVA flows, final displayed/committed input authority, workspace reuse, B/A, independent MQ requests, CRIF/IM, live Rates, replaceable company quant implementations and initial Blazor choice.
- Prior owner design record, `CONTEXT_AND_DESIGN.md` dated 2026-10-18: framework-first Capability AND UI composition, separate four models, retained intermediates, unsaved rebinding, generic-only OSS boundary, existing multi-NS HPC service. That document is background; current input-authority and ID decisions in this package supersede conflicting earlier proposals.
- Earlier mockups are visual context only. They are not proof of real ExGrid, a real agent or real SIMM integration.

## Primary technical references

| ID | Source | What it supports |
|---|---|---|
| S01 | [Claude Code project memory](https://code.claude.com/docs/en/memory) | Root project guidance with concise `CLAUDE.md`; detailed procedures may live in linked/scoped documentation. This is context, not an enforced permission system. |
| S02 | [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) | .NET10 is an LTS line. Pin an approved patch during implementation rather than treating this document as a package lock. |
| S03 | [AG-UI introduction](https://docs.ag-ui.com/introduction) | Event-based interaction between agent runtimes and frontends; distinct from LLM execution and tool/data protocols. |
| S04 | [.NET dependency injection](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/overview) | Interface-based dependencies and registration of replaceable implementations. |
| S05 | [Microsoft application architecture](https://learn.microsoft.com/en-us/dotnet/architecture/modern-web-apps-azure/common-web-application-architectures) | Application-core contracts and outer infrastructure implementations; logical modules need not all be separate services. |
| S06 | [Anti-Corruption Layer](https://learn.microsoft.com/en-us/azure/architecture/patterns/anti-corruption-layer) | A translation boundary for external data models and semantics, inside an app or as a service. |
| S07 | [ExGrid repository](https://github.com/AkiraMisawa/ex-grid) and [ExGrid README](https://github.com/AkiraMisawa/ex-grid/blob/main/src/ExGrid/README.md) | Actual component/source integration entry points. Inspect and pin exact APIs and browser support at implementation time; do not invent a React adapter or assume Safari support. |
| S08 | [gRPC performance guidance](https://learn.microsoft.com/en-us/aspnet/core/grpc/performance?view=aspnetcore-10.0) | If gRPC is chosen, connection reuse and streaming lifecycle need deliberate handling. It is not a requirement to use gRPC for every call. |

## Design judgments

SQLite plus an artifact directory for the demo, the bounded contract vocabulary, per-field automatic/manual ownership, run/case taxonomy, phased implementation and sample endpoint names are design proposals for this application. They are not facts asserted by these external sources.
