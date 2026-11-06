# ADR-0008: One generic create command, and products that register their terms

Status: ACCEPTED 2026-11-06 by the owner, widened the same day (§7, §8) and scoped to describing IRS and CCS
("irs/ccsを記述化するところまで"). Proposed 2026-11-04. Raised by the owner: "Do you think a user can give `new irs` seven arguments?" and
"I want to separate the generic command for creating a trade from the specific ones for each product"
(2026-11-04). Amends ADR-0005 §4 and ADR-0006's `new irs`; changes nothing a Run does.

## Context

`new irs` (ADR-0006) takes seven required positional slots: currency, index, end, notional, direction, fixed and
float frequency. Nobody types that. The only alternative is `add line irs` and then filling in about sixteen cells,
both legs included, and the row is unpriceable the whole time ("An IRS has one notional"). `new ccs` does not exist.

The cause is structural. Each product's create command is hand-written in the client (`irsArguments`, `irsTerms`
in `client/src/apps/index.tsx`). Its slots are positional. Nothing supplies a value the user left out, because
ADR-0005 §4 allows only defaults that the user's own data or a confirmed decision defines, and none is defined yet.
A second product means a second copy. A product in another domain (a swaption, an XVA draft trade) would mean a
third.

## Decision

1. **One generic command in the kernel.** `new <product> <terms…>` is one command line entry and one command
   capability (`entity.create`, effect `EditWorkspace`, ADR-0005), in the composition kernel. It knows nothing of
   rates, legs or currencies. It reads the terms against the product's registration, fills what the registration's
   defaults supply, refuses with every missing term by name, and writes one `Create` edit (ADR-0004) at the
   revision the page saw. The page, the command line and the AI all take this path.
2. **A product registers its terms.** A domain module registers each product it can create, against the workspace
   app it lives in (`rates.irs`, `rates.ccs`):
   - its **fields**, each with a **form** from a small generic set the kernel reads: `period` (`10y`, `3m`), `date`,
     `amount` (`3bn`, `500mm`, `20k`; a bare number is in millions and a bare number of a million or more is refused
     as a full amount typed out of habit, owner 2026-11-04; `m` is never millions, because `6m` is six months and the
     two forms must never read the same word), `percent` (`1.25%`, or `5bp`), `code` (one of a list the module serves, such as currencies),
     `choice` (named words, such as `pay` and `rec`) and `flag`. A form says how a word is read, never what it means.
   - which fields are **required**, and which may be typed **without a name**. A field typed without a name is one
     whose form no other such field shares (an IRS's `end` is its only unnamed period; its frequencies are named,
     `fixed=12m`), or one of a **sequence**: fields of one form filled in the order declared (a CCS's two
     currencies, leg 1 then leg 2).
   - its **default sources**, in order (§4).
   - its **resolver**, which judges whether the terms make a priceable line (today's `LineResolver`), and its
     **field mapping** onto the entity's flat fields (`leg1.notional`, `leg2.notional` from one `notional`).
3. **Words in any order, read by their form.** `new irs jpy 10y 3bn rec 1.25%` and `new irs 10y` both work. A word
   whose form matches exactly one unfilled field fills it. A word that matches none, or more than one, is refused
   by name with what it could be; it is never guessed. `name=value` always works and is the only way to set a
   field that has no unique form (`spread=0`). The command line completes each word from the registration
   (ADR-0006 §6).
4. **Defaults, and where they may come from (amends ADR-0005 §4).** A field the user left out takes, in order:
   a. **The product's template for the resolved convention profile**, a confirmed decision (C41). For example,
      the JPY TONA profile's standard IRS: index TONA, fixed 12m, float 12m. A profile without a confirmed template
      supplies nothing. The development profiles' templates say `unconfirmed`, as their conventions do.
   b. **The workspace's defaults**, set by the user in the workspace (currency, notional, direction). This is the
      user's own data, as ADR-0003's main CSA is.
   An IRS's own quotes are already a confirmed default (C47, 2026-11-06): with no quote it is at the money,
   spread 0 and the fixed leg at par, whatever its profile.
   c. Nothing. The field stays empty and is refused by name if it is required.
   Every defaulted field is recorded in the `Create` edit with its source (`profile:jpy-tona@v/irs`,
   `workspace`). The row marks it as defaulted, distinct from what the user typed and from what the AI wrote. A
   default is never taken from another line, the last input or a market feed.
5. **Commands stay thin wrappers.** `new irs …` and `new ccs …` are the generic command with its product named.
   `rates.line.create` (ADR-0005) becomes `entity.create` with `product: rates.irs`. The AI tool takes the same
   terms as the command line, so it too can leave out what a default supplies.
6. **The server owns the reading.** The client reads words by form only to complete and to show the pending
   command. The server reads them again from the registration and decides (ADR-0003). A registration the client
   has not loaded leaves the command unavailable, never guessed.

7. **A product is a description, not code (owner, 2026-11-06).** A product's fields, forms, required fields and
   sequences are data: a `ProductDescriptor`. Descriptors come through a **catalog port** (`IProductCatalog`). In
   this repository a module writes them; at the company the analytics engine publishes the products it supports and
   their input schemas, and an adapter reads that catalog into descriptors without a kernel or UI change. What a
   description cannot say stays with the product's module, behind a narrow interface: the defaults it supplies, the
   mapping onto the entity's fields, and the **economic check** (today's `LineResolver`). A shape that matches a
   schema is not proof that the terms make economic sense (CLAUDE.md), so the two checks stay apart.
8. **Pricing is found by product.** A pricing provider declares the products, and their versions, it can price.
   Price finds the provider for each line's product; a line whose product no provider prices is listed as not
   priced with that reason, never priced by another provider and never by a mock (CLAUDE.md: no silent fallback).
   A product may therefore be described, created and saved before anything prices it.

Scope of the first implementation (owner, 2026-11-06): the kernel's descriptors, catalog port, word reader and
generic create; IRS and CCS described by the Rates module; the provider declaring both. Loading the engine's catalog,
and a generic leg-by-leg structure for products no description covers, are later work.

## Consequences

- The kernel gains a product registry (fields, forms, required, defaults, mapping), `entity.create`, and the
  generic word reader. These are domain-free: a test registers a non-financial product (a station reading) and
  creates it without a kernel change (as CO01).
- The Rates module registers `rates.irs` and `rates.ccs`. `irsArguments` and `irsTerms` leave the client.
- `ConventionProfile` gains optional product templates. They are confirmed per profile with the convention table
  (C41, still OPEN). Until then only the workspace's defaults and what the user types fill a line.
- The Rates workspace gains defaults the user can set (currency, notional, direction), saved as workspace context
  edits, so they are in the log.
- `new irs` with seven positional slots is removed (ADR-0006's slots remain the mechanism; they become named and
  form-read).

## Open

- Whether the workspace's defaults are the right second source, or whether the owner wants per-user defaults
  across workspaces (that needs the user settings store, C30).
- The confirmed templates per profile (with C41).
- Whether `add line <product>` (an empty line) stays once `new <product>` needs only the end.
