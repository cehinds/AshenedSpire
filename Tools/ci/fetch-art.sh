#!/usr/bin/env bash
# Restores the gitignored Unity/Assets/Art/Imported images on a CI runner (D-1300, docs/BUILD-WEBGL.md).
#
#   Tools/ci/fetch-art.sh <web-checkout-dir>
#
# <web-checkout-dir> is cehinds/ashenspire at Tools/ci/art-pin.json#webCommit (the composite action
# .github/actions/fetch-art checks it out; locally clone and check it out yourself). Steps:
#   1. check the web's art-release.json pins the tag art-pin.json names;
#   2. the web's tools/fetch-art.mjs downloads and sha256-verifies the high and common packs into <web>/.art-cache/<tag>
#      (a cache restored by actions/cache is reused when it still verifies);
#   3. npm ci in Tools (sharp), then scan-assets --release ... --import converts them into Unity/Assets/Art/Imported;
#   4. Tools/ci/check-art.mjs fails unless every id the registry marks imported is on disk.
# Env: ART_REPO_TOKEN (optional; the art repo is public, a token only raises the rate limit).
set -euo pipefail

web="${1:?usage: Tools/ci/fetch-art.sh <web-checkout-dir>}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
web="$(cd "$web" && pwd)"

tag="$(cd "$root" && node -p "require('./Tools/ci/art-pin.json').artTag")"
pinned="$(cd "$web" && node -p "require('./art-release.json').tag")"
if [ "$tag" != "$pinned" ]; then
  echo "::error::Tools/ci/art-pin.json names art tag $tag but the web at that commit pins $pinned; update art-pin.json"
  exit 1
fi

# Node's fetch ignores HTTPS_PROXY unless asked (only matters behind a proxy; harmless otherwise).
if [ -n "${HTTPS_PROXY:-}${https_proxy:-}" ]; then export NODE_USE_ENV_PROXY=1; fi
# The runner's default GITHUB_TOKEN cannot read another repository and would only print a warning.
unset GITHUB_TOKEN
if [ -n "${ART_REPO_TOKEN:-}" ]; then export ART_REPO_TOKEN; fi

echo "::group::fetch art release $tag"
(cd "$web" && node tools/fetch-art.mjs --pack high,common)
echo "::endgroup::"

echo "::group::npm ci (Tools)"
(cd "$root/Tools" && npm ci --no-audit --no-fund)
echo "::endgroup::"

echo "::group::import into Unity/Assets/Art/Imported"
(cd "$root" && node Tools/scan-assets.mjs --release "$web/.art-cache/$tag" --web "$web" --import)
echo "::endgroup::"

(cd "$root" && node Tools/ci/check-art.mjs)
