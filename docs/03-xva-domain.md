# XVA domain and fixed-pricer behavior

This specification describes the first fixed XVA application on the composition platform. It does not define the generic platform in terms of XVA. Fixed UI, agent tools, and authored workflows invoke the same business contracts; they do not replay each other's UI actions.

**Status vocabulary:** **CONFIRMED** is a user requirement from the design conversation. **DEFAULT PROPOSAL** is an implementable starting policy, not a historical production fact. **OPEN** identifies a domain decision that must be resolved before enabling the affected behavior. Later explicit user decisions take precedence.

## 1. Non-negotiable input authority

**CONFIRMED:** The committed, validated typed values represented by the fixed pricer at Price time are the authoritative calculation input. This includes user edits made after automatic following of a main selection.

The server validates this input and converts it to analytics representations. It must not silently reapply a main CSA, default curve, baseline patch, or fresh download and thereby change the submitted meaning. A conversion of units or identifiers is permitted; a change in economic assumptions is not.

The following are distinct records:

1. An immutable baseline snapshot explaining the starting data.
2. Edit intent/provenance explaining what the user changed and which values were derived.
3. An immutable accepted submission containing the final effective input.
4. Engine-specific requests actually produced from that submission.

Items 1 and 2 explain item 3; they are not instructions to replace item 3 with another calculation input. A browser grid's DOM text, formatting, viewport, or transient editor buffer is not the submission model.

## 2. Screen-to-model map

Names below describe responsibilities. Align serialized property names with the canonical contracts and examples in this package; do not create duplicate synonymous models.

| UI area | Model responsibility | Important identity or rule |
|---|---|---|
| Value date | Evaluation date and calendar context | The default is the latest applicable business date; a past date is allowed. Use an agreed business calendar, not the browser's local `today`. |
| Counterparty autocomplete | Selected counterparty registration plus transient search text | A registration distinguishes the counterparty code and legal entity. Search text is not its identity. |
| Netting-set grid | Available master agreement/netting-set choices and a main marker | Keep the business ID, legal closeout-netting status, eligibility, and source version. |
| Credit-source selector | Single-name or proxy credit configuration | Selection determines which quote structure and proxy attributes apply. |
| CDS grid | Quote identifiers, CDS recovery, and tenor spreads | Preserve typed units and full precision behind formatted cells. |
| Proxy selectors | Region, Sector, Rating | These drive the selected proxy mapping; retain the mapped curve and its version. |
| CVA recovery input | Nullable user override with documented analytics fallback | Empty/null remains empty/null; analytics uses the applicable submitted CDS recovery. |
| CSA grid | Available and user-created CSAs, attributes, and a main marker | Multiple CSAs can exist under one master agreement. Main is a default, not a permanent constraint on every trade. |
| Existing/New trade grids | Trade instances, immutable trade inputs, final assignments, and B/A membership | Each row has stable identity independent of row index and sorting. |
| Scenario editor | Base scenario and optional explicitly defined scenarios | With no additional scenario configured, use the base scenario only. |
| Pricing options | Requested measures, risk settings, iterative mode | Capture resolved settings, not only toggle captions. |

### 2.1 Counterparty identity

**CONFIRMED:** The fuzzy-search display format is:

`{counterparty code} | {legal entity code} | {counterparty name} | {special flag}`

The same counterparty code may exist under different legal entities. Use a stable `CounterpartyRegistrationId` or an explicitly validated composite key. Do not key the workspace only by counterparty code, name, or the formatted display string. Return structured search options from the server, including the IDs and display fields.

Changing autocomplete text without selecting a valid option must not silently retain an unrelated selected registration. Debounce search, cancel obsolete requests where possible, and discard stale responses by search generation.

### 2.2 Master agreement and netting set

The production screen initially selects a counterparty, then an available master agreement, and loads its pricing data. The current conversation often uses master agreement and netting set together. **OPEN:** confirm the exact cardinality in company data; do not make a globally reusable schema assume they are interchangeable. Store separate business identifiers when the source exposes them.

**CONFIRMED:** Show whether legal closeout netting is effective. This affects analytics grouping materially.

- Preserve the actual selected business netting-set ID in the common model.
- Preserve the legal status and the scope to which it applies.
- The company engine adapter implements the existing encoding: eligible trades use the analytics grouping value `Netting`; otherwise each trade uses its trade ID as its analytics netting-set value.
- That encoding is local to the relevant analytics request. Never merge unrelated agreements because they share the literal `Netting`.
- Do not infer legal effectiveness from the presence of a CSA or from a main marker.
- **DEFAULT PROPOSAL:** display legal status as source-controlled/read-only in the initial UI. Hypothetical legal-status overrides require an explicitly enabled what-if feature and provenance.

### 2.3 Credit configuration

**CONFIRMED:** The CDS grid includes ticker, currency, seniority, documentation clause, CDS recovery, and tenor columns such as 6M, 1Y, through 20Y. Reference-entity display name may be omitted initially. Single-name and proxy curves expose a term structure, and spreads can be overridden.

For proxy credit, display editable Region/Sector/Rating selections above the grid. Record both the mapping inputs and the resolved curve identity/version. A new mapping response may update automatically bound quotes; it must not overwrite later edits from an obsolete request.

**CONFIRMED:** CDS recovery and the optional CVA recovery override are separate fields. When `cvaRecoveryOverride` is null/empty, preserve that absence through submission and native mapping; analytics automatically uses the applicable CDS recovery from the pinned input. The client and server must not synthesize a recovery override or require an `effectiveCvaRecovery` field. Preserve the applicable CDS curve/recovery and its source version so the documented fallback is reproducible. Zero is an explicit recovery override, not an empty value. Validate that the required applicable CDS input is available; do not replace it with a newly fetched value at Price time.

Represent spreads with an explicit unit, for example basis points. Validate required tenors, finite values, recovery ranges, and engine support. Engine-specific calibration restrictions belong to provider validation and must produce a clear unsupported/invalid-input result.

### 2.4 CSA configuration

**CONFIRMED:** A master agreement can offer multiple CSAs, all listed in the CSA grid. One is marked main. Users can create a hypothetical CSA, assign different trades to different eligible CSAs, and override trade discount curves.

Creating a CSA is optional in the new-counterparty flow. Support an explicit **uncollateralized** state, including a dummy-counterparty context used for a new prospect. **CONFIRMED:** an uncollateralized trade's `csaId` is the master-agreement-bound sentinel `nocsa_{ma id}`, for example `nocsa_MA-001`; it is not null. The sentinel is a valid explicit no-CSA assignment, not a physical CSA record, and it must match the trade's master agreement. An empty collection of real CSA records is valid. Distinguish this state from an unresolved collateral choice and do not fabricate a physical CSA to satisfy a grid or foreign key. If collateralization is configured through a real CSA, resolve a valid eligible CSA before submission.

**CONFIRMED:** the default uncollateralized discount curve is configurable, for example `jpy_ois`; it is not a universal hard-coded financial rule. Resolve the applicable baseline configuration profile/source during context preparation, record its identity/version, and apply the resolved default to automatically bound UI fields. The resulting curve must be visible and valid for the request. A later manual curve edit wins in the frozen submission. Changing deployment configuration after preparation does not authorize a Price-time replacement of a displayed curve.

The initial model must allow at least currency, thresholds, MTA, and discount-curve association. **OPEN:** settle the full company CSA schema and the directionality of thresholds/MTA, collateral currency sets, rounding, independent amounts, and margin timing before claiming that this subset reproduces company collateral behavior. Do not flatten distinct party-facing values into one number merely to simplify the grid.

- Source CSAs have stable source IDs and immutable source versions.
- User-created CSAs have workspace identity and a versioned definition, never a fabricated source-system ID.
- Main CSA is selected from the eligible set, including eligible user-created CSAs.
- Existing trades preserve their original valid CSA assignment unless a documented action changes it.
- New trades receive the current main CSA as a starting default when the context is collateralized; explicitly uncollateralized new trades receive the matching `nocsa_{ma id}` sentinel and the configured default curve through automatic following.
- If a trade's curve is automatically bound to its CSA, changing that trade's CSA updates the curve. A later explicit curve edit is retained in the final submitted input.
- A CSA or curve that becomes ineligible causes a visible validation issue. Do not silently replace it with another selection.

## 3. Trade inputs live before and beyond a Run

**CONFIRMED:** Users reuse a tab/workspace and run the same loaded draft trades with different configurations. Downloading a draft trade must not require a Run to exist.

Use two concepts:

- **`TradeInput`**: immutable downloaded trade content, source identity/version, acquisition metadata, and artifact references where needed.
- **Trade instance**: an editable workspace row referencing a `TradeInput`, with a stable row ID, final CSA/curve assignments, editable fields supported by the UI, and B/A membership.

Booking ID alone is not a sufficient version identity. A refresh can produce a new `TradeInput`; old Runs retain their previous input references. Repricing does not automatically refetch a draft, turn it into an existing booked trade, or make After the new Original.

Rows expose the relevant trade type, cashflow currency or currencies, maturity, MET date where supplied, booking ID, CSA, and discount curve. Keep the source meaning of MET date; do not invent an expansion or business rule for the acronym.

**DEFAULT PROPOSAL:** Repeatedly importing the same source ID/version focuses the existing row or offers an explicit duplicate-instance action. Intentional duplicates get different trade-instance IDs. Never deduplicate actual exposures solely by display text.

## 4. Before and After membership

**CONFIRMED:** The last two trade-grid columns contain independent Before and After checkboxes. Both headers have select/clear-all controls.

| Before | After | Meaning in normal mode |
|---|---|---|
| On | On | Include the row in both portfolios. |
| On | Off | Remove the row from After. |
| Off | On | Add the row to After. |
| Off | Off | Keep it in the workspace but exclude it from this calculation. |

**DEFAULT PROPOSAL:** Existing rows initially have both checked; newly loaded draft rows initially have Before unchecked and After checked. Make these defaults explicit in the implementation.

Header operations target the declared complete grid dataset, not rendered virtual rows. In the initial design, 'All existing trades' and 'All new trades' operate over their entire respective datasets, including rows outside the current viewport. If filtered selection is added, expose it as a separately labelled operation with a count. Do not silently reinterpret the meaning of 'all'.

### 4.1 Scope of editable fields

The two membership flags do not by themselves define whether an edit changes Before, After, or both.

**DEFAULT PROPOSAL:** A single shared value editor applies to both selected sides. To offer an After-only modification, expose its scope explicitly and materialize both effective values in the accepted input. Initially keep economic fields read-only unless their editing and scope rules are specified.

**OPEN:** Confirm the company rules for side-specific changes such as maturity, mandatory break/MET date, notional, CSA, and master-agreement reassignment. If the existing application has per-field `useAsDiff` semantics, preserve them through a documented field-scope map; do not infer or discard them. This decision is required before enabling those individual editors, but need not block the normal add/remove workflow.

## 5. Individual-addition iterative pricing

**CONFIRMED:** The initial iterative mode prices each selected new trade individually. It is not a cumulative sequence. A future 'two trades form one package' feature is out of the initial scope.

Define the visible selection explicitly:

- `B`: the fixed Before portfolio.
- `E`: the existing-trade selection for After.
- `T_i`: each eligible new trade selected for After.
- Case `i`: compare `B` with `E + T_i` under the frozen configuration.

`candidateTradeInstanceIds` is a derived consistency projection of the frozen grid state: it must contain exactly the eligible New rows with After checked, with no duplicates. It is not a second independently editable selection that can omit checked rows. Require at least one candidate for individual-addition mode and reject a mismatch instead of silently selecting a different trade set.

If the existing After selection differs from Before because the user removed existing trades, that common change is present in every case and must be shown in the case description. Do not label the resulting difference as solely the standalone new trade effect.

**DEFAULT PROPOSAL:** In initial iterative mode, require participating new trades to be excluded from Before. If new-trade Before checkboxes create an ambiguous baseline, return a targeted validation issue; never silently uncheck or omit a submitted row. Normal mode continues to support all four B/A combinations.

One Price press creates one `AnalysisRun`, containing one `Case` per new trade. Bound case concurrency and expose per-case progress, failure, and cancellation status. A failed case is not a zero-valued result.

### 5.1 Independent engine requests

**CONFIRMED:** Before and After are sent separately to analytics through MQ. A case may also require distinct FVA/non-FVA configurations, base/risk calculations, and preparation such as CRIF generation.

Do not assume one Run, Case, or CapabilityInvocation equals one MQ message. Record the logical `AnalyticsRequest` for each fixed engine input, its side/purpose/configuration, and each `DispatchAttempt`.

Initial implementation performs the required Before/After calculations independently for each case. Shared-Before reuse is an optional later optimization only after full trade inputs, market and model versions, configuration, provider semantics, stochastic settings, and permission/retention scope are proven equivalent. Retain case-to-result lineage if reuse is later added.

## 6. Context changes and baseline lifetime

**CONFIRMED:** Value date, counterparty registration, and main netting-set changes cause automatic downstream downloads. The same Original used to display and edit inputs must remain the baseline for that workspace revision; Price does not silently replace it.

| Trigger | Required downstream work | Input treatment |
|---|---|---|
| Value date change | Reload date-dependent credit, portfolio, and any date-dependent reference/market data for the selected context. | Stage a new context revision; preserve the previous accepted state until the new state can be adopted atomically. |
| Counterparty registration change | Reload available agreements/netting sets, select or resolve a valid main choice, then load credit/CSA/trade data. | Previous counterparty baseline cannot become the new counterparty baseline. |
| Main netting-set change | Load eligible CSAs, its existing trades, and related context data. | Reconcile assignments and show invalid retained edits. |
| Main CSA change | Reconcile eligible automatically bound row assignments/curves; fetch necessary CSA-dependent data. | Do not reload an entire portfolio unless the provider actually requires it. |
| Explicit refresh | Acquire new source versions and show/adopt a new baseline revision. | Preserve old Run inputs and artifacts. |

Preparation also resolves the applicable configurable baseline/default profile, including the uncollateralized discount-curve setting, from its recorded source/version. Adopt that configuration with the prepared context. Price captures the resulting effective values and explicit empty-override semantics; it does not rerun configuration resolution against a newer profile.

**DEFAULT PROPOSAL:** Retain new draft trade inputs in a staging area across date/counterparty/netting-set changes, but invalidate context-dependent assignments and revalidate before including them in pricing. Replace the existing-trade baseline for the new context. This policy should be explicit in UI copy; it is not permission to carry an old counterparty's existing portfolio into another one.

When multiple main choices are available, use a documented server-supplied default only when its selection policy is valid for the operation. AI requests with unresolved financially material choices must return a choice interaction. Never select the first array element as an implicit financial default.

## 7. Automatic following and manual edits

**CONFIRMED:** A manual edit after an automatic update takes precedence in the final displayed and submitted data.

**DEFAULT PROPOSAL:** Track field ownership, for example `Source`, `Automatic`, or `User`, plus its dependency key/revision. A later main-selection change updates fields still bound to that default; a user-owned value remains until reset to automatic or until it becomes invalid. Provide an explicit reset-to-automatic action.

This proposal has not been confirmed for every future main-change case. Keep it in one reconciliation policy, not scattered among component callbacks. An alternate approved policy can replace it without changing input authority.

When a manually selected CSA becomes invalid under a new main netting set, show the issue and require a valid selection or explicit reset. Retaining the user's intent does not make an ineligible relationship valid.

## 8. Required domain acceptance evidence

1. Select a registration, load two new trades, change main CSA, manually change the second trade's CSA/curve, then Price while its editor is active. The committed second-row values reach the adapter unchanged in meaning.
2. Change value date twice while downloads are in flight. Responses for the earlier date cannot alter the later context.
3. Use two registrations sharing a counterparty code under different legal entities. Their agreements and portfolios remain distinct.
4. Set an existing row to Before-only and a new row to After-only. Frozen portfolios include exactly those memberships.
5. Select/clear all with virtualized rows and filtering. The declared scope and submitted membership agree.
6. Run three selected new trades in iterative mode. Each case contains one new trade, not an accumulating prefix. Show any common existing-portfolio change.
7. Reprice with changed settings after a Run completes. The same immutable draft input is reusable; the previous Run is unchanged.
8. Verify a legally non-nettable agreement maps to trade-specific analytics grouping, without changing the common business identifiers.
9. Distinguish CDS recovery from CVA recovery in editing, serialization, validation, and adapter mapping. A null/empty CVA override remains absent and analytics uses the pinned applicable CDS recovery; an explicit zero remains zero.
10. The engine adapter rejects unsupported specified inputs rather than substituting defaults, dropping fields, or returning invented zeroes.
11. An uncollateralized row uses the correct `nocsa_{ma id}` sentinel without requiring a physical CSA. Preparation applies the configured default curve, and a later manual curve edit survives submission.
