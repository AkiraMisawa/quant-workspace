# Current to new: migration and supersession map

| Current behavior / earlier proposal | Target | Migration technique |
|---|---|---|
| Fluxor effects trigger other effects and page state owns orchestration | Small workspace state + pure transitions + preparation coordinator + application services | Implement new page beside old page; do not rewrite old global state in place. |
| Original refreshed/overwritten around Price | Immutable BaselineSnapshot with context and source versions | New flow pins prepared baseline; legacy history marked with known fidelity. |
| Client produces effective Before/After payloads | Final committed full typed input remains authoritative; case construction is explicit | Preserve economic meaning and freeze input, while moving transport/build concerns into services. |
| Earlier recommendation sends only user overrides | Superseded for fixed XVA UI | Keep edit provenance for explanation; never use it to recreate a different accepted input. |
| UI session identifies a folder tree | Workspace is editable; Run is immutable; UiSession is incidental | Preserve existing SessionId as legacy metadata and route old IDs through lookup. |
| One Run folder contains many unnamed artifacts | Logical cases/invocations/requests/attempts plus role-tagged artifacts | Index old files without inventing missing lineage; new runs record structured relations. |
| Draft trade first appears in a Run folder | Immutable TradeInput exists before any Run | Workspace references it; later runs reuse the fixed version. |
| File system is both work area and history | Metadata DB + authoritative immutable artifact store | Staging is temporary; one byte owner per retained artifact. |
| Before and After ZIPs sent independently | Preserved | Separate request identities with explicit pairing and scenario/config scope. |
| Multiple configs/risk/CRIF calls hidden inside one action | Explicit dependency and execution lineage | Reuse actual provider scheduling and record its returned references. |
| Existing company service partitions bulk providers onto HPC | Preserved | Adapter translates common input and maps aggregated output; no replacement scheduler. |
| Ten browser tabs for ten-counterparty comparison | One workflow submission with ten Cases | Domain provider may use supported bulk execution; UI presents one grouped analysis. |
| Fixed pages define all available views | Independent validated View definitions | Add optional views and row actions to retained typed data without changing pricing logic. |
| Quant library tightly coupled to the host | Separate worker/service with replaceable adapters and source ports | Standalone build has no private/native dependencies in Web. |

## Do not overinterpret historical evidence

An old `request.json` may contain the effective before/after input but not the user's exact sequence or intent. An overwritten Original cannot be recovered merely by subtracting fields. Store what exists, its source/fidelity and missing information. User-defined final settings, user edit events and analytics payloads are different records.

## Same-process rollout

New routes, services, state containers and feature policies can coexist in the existing application host when target frameworks and packages are compatible. A hidden navigation link is insufficient; protect new API routes server-side. If adopting .NET10 requires changing the existing .NET8 host, treat that host upgrade as a separate compatibility change; simply adding a .NET10 project cannot make it run inside an unchanged .NET8 process.

Native computation remains in a separate worker even when old/new web pages share a process. Feature flags/entitlements route users; rollback disables new routes without altering old run folders. Legacy history can remain accessible through a read-only locator until verified migration is worthwhile.
