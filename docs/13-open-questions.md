# Open questions and bounded defaults

These questions are not an instruction to ask the owner everything before coding. Apply the stated default, discover what can be observed, and ask only when a material choice blocks the current slice.

| Question | Default / useful work now | Gate |
|---|---|---|
| Exact ExGrid APIs and browser support? | Inspect/pin the repository and run a real component spike. Do not imitate its API. | P0; actual mobile release |
| ~~Blazor hosting mode?~~ Superseded by ADR-0001 (React client). | Preserve existing hosted-WASM boundaries where integrating; choose a documented .NET10 mode for standalone demo after a small spike. | P0 |
| Main change after manual override? | Sticky field ownership with explicit reset to automatic is proposed. | Domain behavior review before rollout |
| Which fields affect Before, After or both? | Keep known B/A and CSA/curve semantics explicit; initial economic fields may be display-only. Never infer old `useAsDiff` behavior. | Relevant XVA field editing |
| Exact MA-to-NS relationship and CSA scope? | Separate IDs and validate via source data; fixture may use one-to-one. | Company source integration |
| Full CSA financial terms? | Use a typed starter subset plus immutable source data; unsupported edits are explicit. | Each newly editable term |
| What survives CP/date/NS changes? | Stage imported new trades, clear incompatible assignments, revalidate. Existing portfolio follows the new baseline. | Domain review |
| Iteration with candidate already in Before? | Reject that initial iterative combination; normal B/A still supports all four states. | Iterative feature |
| QuantLib, Quantra or another worker? | Prove real IRS pricing and isolation; compare product/OS/package coverage. | P0/P4 |
| First XCCY form and fair quantity? | Proposed non-resetting float/float basis swap; confirm conventions and target leg before claiming support. | XCCY slice |
| Agent runtime/model/auth? | Company agent is GitHub Copilot with selectable models (C34). Keep the adapter port; scripted runtime built (ADR-0007, `IAgentRuntime`); how the server reaches Copilot and authenticates was asked of the owner on 2026-10-27; verify Copilot's actual integration surface before claiming it works. | P5 |
| Unattended company AI authentication? | Test actual supported credentials/entitlements and operational policy. Do not assume personal subscriptions work on a server. | Company AI integration |
| Workflow runtime? | Bounded reference executor behind a port; evaluate existing runtimes before broadening. | Durability/scale expansion |
| Exact company `IDataProvider`/ZIP/action/scenario grammar? | Use opaque artifacts and synthetic mappings; inspect actual interface/parser/version. | Company XVA adapter |
| MQ idempotency/cancellation/reconciliation? | Durable attempt/correlation/inbox state; unknown outcomes stay unknown until reconciled. | Real MQ dispatch |
| Production DB/retention/HA? | SQLite in production first (C35). Retention rule per C33; the period itself is still to be set. HA/multi-instance open. | Multi-instance company rollout |
| Display charge and CVA sign rules? | Keep raw signed CVA and explicitly named delta; charge ratio is a separate measure. | Charge display |
| Exact public OSS licensing/package split? | Keep generic modules independently buildable and private domain boundaries. | Any publication |
| Performance thresholds? | Measure documented workloads first, then agree targets. | Performance acceptance |

## Escalation format

When a question actually blocks work, report: the concrete choice, observed evidence, a recommended default, the financial/operational impact, and what has already been completed. Ask a focused question, not for approval of an abstract architecture before doing useful work.
