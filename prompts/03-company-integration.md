Integrate the existing application with the company's actual data and quant execution interfaces. First read `docs/08-provider-boundaries.md`, `docs/05-execution-history.md`, `docs/06-persistence-migration.md`, `docs/13-open-questions.md`, and the current implementation status.

Inspect the real source interfaces, data/provider schemas, versions, package feed and deployment constraints. Do not invent `IDataProvider` methods, action names, scenario grammar, MQ idempotency or authentication support.

Implement company-only adapters/packages for the required capabilities and sources. Reuse the existing service that accepts multi-counterparty/netting-set providers, partitions work to HPC and aggregates outputs. Keep its partitioning internal to the company capability implementation. Preserve independent Before/After requests and explicit case/output bindings.

Preserve the immutable accepted input's economic meaning. Map identifiers and units explicitly; reject unsupported specified settings. Do not reconstruct different XVA inputs from defaults or fetch newer original data at execution time.

Configure providers per capability at the composition root. Standalone/core builds must remain possible without company SDKs. A missing or failing company provider must not silently route to a mock.

Verify contract compatibility and numeric agreement separately using representative reference trades/portfolios with matched calendars, conventions, market, model and stochastic settings. Record differences and approved tolerances. Keep evidence of provider/adapter/engine/data versions and raw artifacts.

Expose the new pages through developer-only routes and server policies alongside the existing app, using compatible target frameworks. Do not assume an unchanged .NET8 host can load a .NET10 application. Keep legacy history readable by RunId through indexing before considering physical movement.

Complete useful adapter/mapping/test work and report concrete remaining decisions. Follow the actual company release process for deployment; this prompt does not authorize publishing proprietary code or replacing production services.
