# Quant Workbench

A keyboard-first workbench in which capabilities, workflows, views and context are composed rather
than hard-wired into pages ([ADR-0002](docs/adr/0002-the-product-is-a-keyboard-first-workbench-and-the-generic-slice-comes-first.md)).
Start with `START_HERE.md` and `docs/00-decisions.md`; what runs today is in
[`IMPLEMENTATION_STATUS.md`](IMPLEMENTATION_STATUS.md).

## Layout

| Path | Contents |
|---|---|
| `server/` | ASP.NET Core host and the composition kernel (`Workbench.slnx`, .NET 10) |
| `client/` | React + TypeScript client (Vite, Mantine, TanStack Query, Zustand, ExGrid React) |
| `vendor/ex-grid/` | ExGrid packages and stylesheet packed from a pinned ex-grid commit (`PIN`) |
| `docs/` | Requirements, ADRs (`docs/adr/`), plans and reviews |

## Commands

The toolchain (.NET SDK 10.0.203, Node 26.12.1) comes from the Nix flake. Run each command from the
repository root, or prefix it with `nix develop <root> -c`.

```sh
# Server: build and the xUnit suite (composition, validation, API)
nix develop -c dotnet test server/Workbench.slnx

# Client: install, typecheck, unit tests (Vitest)
nix develop -c bash -c 'cd client && npm ci && npm run typecheck && npm test'

# Client build: written into server/src/Workbench.Web/wwwroot, which the host serves
nix develop -c bash -c 'cd client && npm run build'

# Run the workbench on http://localhost:5080 (after the client build). Workspaces, Runs and results are
# kept in server/src/Workbench.Web/App_Data/workbench.db unless Workbench__Database names another file.
nix develop -c dotnet run --project server/src/Workbench.Web --urls http://localhost:5080

# Development: Vite on http://localhost:5173, forwarding /api to the host above
nix develop -c bash -c 'cd client && npm run dev'

# Browser tests (Playwright); start the host themselves unless one is already on the port
nix develop -c bash -c 'cd client && npm run build && npx playwright test'
```

Playwright uses its own Chromium (`npx playwright install chromium`). Where a Chromium is already
installed for another Playwright version, name it with `PLAYWRIGHT_CHROMIUM=/path/to/chrome`.
`WORKBENCH_PORT` moves the host to another port. Each browser run starts the host on a database file of its own in the
temporary directory (`WORKBENCH_DB` names one instead).

To re-vendor ExGrid from another commit: check out the commit in an ex-grid clone and run `tools/ex-grid/vendor.sh <ex-grid checkout>`; it writes the commit into `vendor/ex-grid/PIN`.

## Rules

`CLAUDE.md` holds the non-negotiable requirements. Everything committed is in English.
