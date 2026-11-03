#!/usr/bin/env bash
# Starts the agent host and the workbench with the Copilot runtime (ADR-0007 §5, C44).
#
#   tools/run-with-copilot.sh --model <id> [--port 5080] [--host-port 5395] [--token-file <file>]
#       The host runs as you; only the host is given the token. It is taken from, in this order:
#       COPILOT_GITHUB_TOKEN in this shell, --token-file, or the macOS Keychain item named below
#       (store it once: tools/run-with-copilot.sh --store-token).
#
#   tools/run-with-copilot.sh --store-token
#       Has macOS's security tool ask for the token (not echoed) and keeps it in the login Keychain.
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
model="" port=5080 host_port=5395 as_user="" host_dir="" token_file="" install_dir="" store_token=""
keychain_item=quant-workbench-copilot

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
    --store-token) store_token=1; shift ;;
    *) die "unknown argument $1 (see the head of this script)" ;;
  esac
done

cd "$root"

if [[ -n $store_token ]]; then
  command -v security >/dev/null || die "--store-token uses the macOS Keychain; elsewhere, use --token-file"
  # With -w last and no value, security asks for the token itself (twice, not echoed): it never appears on a
  # command line or in this script's variables. -U replaces an earlier one.
  echo "Paste the Copilot token (github_pat_...) when asked, twice."
  security add-generic-password -U -a "$USER" -s "$keychain_item" -w
  case $(security find-generic-password -a "$USER" -s "$keychain_item" -w) in
    github_pat_*|gho_*|ghu_*) ;;
    *) security delete-generic-password -a "$USER" -s "$keychain_item" >/dev/null
       die "the Copilot SDK takes github_pat_, gho_ or ghu_ tokens; the Keychain item was removed" ;;
  esac
  echo "Stored in the login Keychain as $keychain_item."
  exit 0
fi

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
  if [[ -z ${COPILOT_GITHUB_TOKEN:-} && -n $token_file ]]; then
    export AgentHost__Copilot__TokenFile="$token_file"
  elif [[ -z ${COPILOT_GITHUB_TOKEN:-} ]] && command -v security >/dev/null; then
    COPILOT_GITHUB_TOKEN=$(security find-generic-password -a "$USER" -s "$keychain_item" -w 2>/dev/null) \
      || die "no token: COPILOT_GITHUB_TOKEN is not set and the Keychain has no $keychain_item (run --store-token)"
    export COPILOT_GITHUB_TOKEN
  fi
  [[ -n ${COPILOT_GITHUB_TOKEN:-} || -n ${AgentHost__Copilot__TokenFile:-} ]] \
    || die "no token: set COPILOT_GITHUB_TOKEN, pass --token-file, or store it with --store-token (macOS)"
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
env -u COPILOT_GITHUB_TOKEN -u GH_TOKEN -u GITHUB_TOKEN -u AgentHost__Key -u AgentHost__Copilot__TokenFile \
  nix develop -c dotnet run --project server/src/Workbench.Web --urls "http://localhost:$port"
