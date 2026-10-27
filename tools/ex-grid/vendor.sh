#!/usr/bin/env bash
# Packs ExGrid's npm packages and stylesheet from a local ex-grid checkout into vendor/ex-grid,
# and records the commit they came from (ADR-0001: ExGrid is consumed from a pinned commit).
#
#   nix develop -c tools/ex-grid/vendor.sh ../ex-grid
#
# The checkout must be clean, so the PIN names exactly what was packed.
set -euo pipefail

checkout="$(cd "${1:?usage: vendor.sh <ex-grid checkout>}" && pwd)"
repo="$(cd "$(dirname "$0")/../.." && pwd)"
out="$repo/vendor/ex-grid"

if [ -n "$(git -C "$checkout" status --porcelain)" ]; then
  echo "ex-grid checkout has uncommitted changes; refusing to pack an unnamed state" >&2
  exit 1
fi
commit="$(git -C "$checkout" rev-parse HEAD)"

(cd "$checkout/src/npm" && npm ci --no-audit --no-fund && npm run build)

rm -rf "$out"
mkdir -p "$out"
for package in data core react; do
  (cd "$checkout/src/npm/$package" && npm pack --pack-destination "$out" >/dev/null)
done
cp "$checkout/src/ExGrid/wwwroot/ex-grid.min.css" "$out/"
printf '%s\n' "$commit" > "$out/PIN"
echo "Vendored ex-grid $commit into $out"
