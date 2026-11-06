#!/usr/bin/env bash
# Starts the server and the client's dev server together, for working on the workbench.
#
#   tools/dev.sh [--port 5080]
#       The server (ASP.NET Core) on http://localhost:<port>, and Vite on http://localhost:5173, which reloads on
#       save and forwards /api to the server. Open http://localhost:5173. Ctrl-C stops both.
#
# Run it inside `nix develop` or not: it enters the flake's shell itself when dotnet is not on PATH.
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
port=5080

while [[ $# -gt 0 ]]; do
  case $1 in
    --port) port=$2; shift 2 ;;
    *) echo "dev: unknown argument $1 (see the head of this script)" >&2; exit 1 ;;
  esac
done

if ! command -v dotnet >/dev/null || ! command -v npm >/dev/null; then
  exec nix develop "$root" -c "$0" --port "$port"
fi

cd "$root"
[[ -d client/node_modules ]] || (cd client && npm ci)

pids=()
stop() {
  trap - INT TERM EXIT
  for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done
  wait 2>/dev/null || true
}
trap stop INT TERM EXIT

dotnet run --project server/src/Workbench.Web --urls "http://localhost:$port" &
pids+=($!)
(cd client && WORKBENCH_API="http://localhost:$port" npm run dev) &
pids+=($!)

echo "dev: server on http://localhost:$port, client on http://localhost:5173 (open this one). Ctrl-C stops both."
# Whichever stops first stops the other, so a server that failed to start is not left behind a client that cannot reach it.
wait -n
