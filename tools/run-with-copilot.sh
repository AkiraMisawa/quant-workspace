#!/usr/bin/env bash
# Starts the agent host and the workbench with the Copilot runtime (ADR-0007 §5, C44).
#
#   tools/run-with-copilot.sh --model <id> [--port 5080] [--host-port 5395]
#       The host runs as you. COPILOT_GITHUB_TOKEN must be set in this shell; only the host is given it.
#
#   tools/run-with-copilot.sh --install-host <dir>
#       Publishes the host into <dir> (with sudo), readable by every user, for the next form.
#
#   tools/run-with-copilot.sh --model <id> --as-user <user> --host-dir <dir> --token-file <file>
#       The host runs as <user>, from the published <dir>, and reads the token from <file>, which only <user> can read.
#       Your files (the workbench's database, your credentials) are then out of the agent's reach by the OS's rules.
#
# The two processes share a key made afresh for each start; it is passed in the environment, never on a command line.
# The workbench is started without COPILOT_GITHUB_TOKEN, GH_TOKEN or GITHUB_TOKEN. Ctrl-C stops both.
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
model="" port=5080 host_port=5395 as_user="" host_dir="" token_file="" install_dir=""

die() { echo "run-with-copilot: $*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case $1 in
    --model) model=$2; shift 2 ;;
    --port) port=$2; shift 2 ;;
    --host-port) host_port=$2; shift 2 ;;
    --as-user) as_user=$2; shift 2 ;;
    --host-dir) host_dir=$2; shift 2 ;;
    --token-file) token_file=$2; shift 2 ;;
    --install-host) install_dir=$2; shift 2 ;;
    *) die "unknown argument $1 (see the head of this script)" ;;
  esac
done

cd "$root"

if [[ -n $install_dir ]]; then
  staging=$(mktemp -d)
  nix develop -c dotnet publish server/src/Workbench.AgentHost -c Release -o "$staging"
  sudo mkdir -p "$install_dir"
  sudo cp -R "$staging/." "$install_dir/"
  sudo chmod -R a+rX "$install_dir"
  rm -rf "$staging"
  echo "Published the agent host to $install_dir."
  exit 0
fi

[[ -n $model ]] || die "--model is required (the host's /models lists the ids your account may use)"

export AgentHost__Key=$(openssl rand -hex 32)
export AgentHost__Url="http://127.0.0.1:$host_port"
dotnet_root=$(nix develop -c bash -c 'echo "$DOTNET_ROOT"' 2>/dev/null | tail -1)

if [[ -n $as_user ]]; then
  [[ -n $host_dir && -n $token_file ]] || die "--as-user needs --host-dir and --token-file"
  sudo -v
  sudo -H -u "$as_user" --preserve-env=AgentHost__Key,AgentHost__Url \
    env AgentHost__Copilot__TokenFile="$token_file" DOTNET_ROOT="$dotnet_root" \
    bash -c 'cd "$1" && exec "$2/dotnet" Workbench.AgentHost.dll' _ "$host_dir" "$dotnet_root" &
else
  [[ -n ${COPILOT_GITHUB_TOKEN:-} ]] || die "COPILOT_GITHUB_TOKEN is not set; the agent has no credential of its own"
  nix develop -c dotnet build server/src/Workbench.AgentHost >/dev/null
  (cd server/src/Workbench.AgentHost && exec "$dotnet_root/dotnet" bin/Debug/net10.0/Workbench.AgentHost.dll) &
fi
host_pid=$!
trap 'kill "$host_pid" 2>/dev/null || true' EXIT

for _ in $(seq 1 60); do
  kill -0 "$host_pid" 2>/dev/null || die "the agent host stopped while starting"
  if curl -sf -H "X-Agent-Host-Key: $AgentHost__Key" "$AgentHost__Url/health" >/dev/null; then break; fi
  sleep 1
done
curl -sf -H "X-Agent-Host-Key: $AgentHost__Key" "$AgentHost__Url/health" >/dev/null || die "the agent host did not answer at $AgentHost__Url"
echo "Agent host is up at $AgentHost__Url."

export Agent__Runtime=copilot
export Agent__Copilot__HostUrl="$AgentHost__Url"
export Agent__Copilot__HostKey="$AgentHost__Key"
export Agent__Copilot__Model="$model"
env -u COPILOT_GITHUB_TOKEN -u GH_TOKEN -u GITHUB_TOKEN -u AgentHost__Key \
  nix develop -c dotnet run --project server/src/Workbench.Web --urls "http://localhost:$port"
