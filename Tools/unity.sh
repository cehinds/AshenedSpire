#!/usr/bin/env bash
# Run Unity in batchmode for this project (docs/design/09 §6.2).
#   Tools/unity.sh <log-name> [extra Unity args...]
# Env overrides: UNITY_EXE, UPM_CACHE_ROOT (defaults to the per-user Unity cache so a
# machine-level .upmconfig.toml pointing at an unavailable drive cannot break the build).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY_EXE="${UNITY_EXE:-/d/Unity/6000.6.0f1/Editor/Unity.exe}"
LOCAL_APPDATA="${LOCALAPPDATA:-$HOME/AppData/Local}"
export UPM_CACHE_ROOT="${UPM_CACHE_ROOT:-$(cygpath -m "$LOCAL_APPDATA" 2>/dev/null || echo "$LOCAL_APPDATA")/Unity/cache}"
NAME="${1:?log name required}"; shift
mkdir -p "$ROOT/Logs"
LOG="$ROOT/Logs/$NAME.log"
PROJECT="$(cygpath -m "$ROOT/Unity" 2>/dev/null || echo "$ROOT/Unity")"
LOGW="$(cygpath -m "$LOG" 2>/dev/null || echo "$LOG")"
set +e
"$UNITY_EXE" -batchmode -projectPath "$PROJECT" -logFile "$LOGW" "$@"
CODE=$?
set -e
grep -E "error CS[0-9]+|Compilation failed|Scripts have compiler errors" "$LOG" | sort -u | head -40 || true
echo "unity($NAME) exit $CODE — log: Logs/$NAME.log"
exit $CODE
