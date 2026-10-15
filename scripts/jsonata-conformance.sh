#!/usr/bin/env bash
# Runs tests/jsonata-conformance/cases.json through every JSONata implementation we use and compares the
# results with the recorded expectations (see tests/jsonata-conformance/README.md).
#
#   scripts/jsonata-conformance.sh [check|record] [dotnet|js|python|all]    (default: check all)
#
# Prerequisites: .NET SDK 8+, `bun` or `node` (npm) for the JS run, Python 3.10+ (3.13 tested) for the
# Python run. `record` needs all three and rewrites the expectations in cases.json: review the diff.
# Exit code is non-zero if a result differs from its expectation.
set -euo pipefail

mode="${1:-check}"
target="${2:-all}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
suite="$root/tests/jsonata-conformance"
out="$suite/observed"
mkdir -p "$out"

[ "$mode" = "record" ] && target=all

run_dotnet() {
  dotnet run --project "$suite/dotnet/Runner.fsproj" -c Release -- "$suite/cases.json" "$out/dotnet.json"
}

run_js() {
  local runtime
  runtime="$(command -v bun || command -v node || true)"
  if [ -z "$runtime" ]; then echo "need bun or node for the JS run" >&2; exit 1; fi
  (cd "$suite/js"
   if command -v bun >/dev/null && [ "$runtime" = "$(command -v bun)" ]; then bun install --silent; else npm install --silent --no-audit --no-fund; fi
   "$runtime" run.mjs "$suite/cases.json" "$out/js.json")
}

run_python() {
  local venv="$suite/python/.venv"
  if [ ! -x "$venv/bin/python" ]; then
    local py
    py="$(command -v python3.13 || command -v python3.12 || command -v python3.11 || command -v python3.10 || true)"
    if [ -z "$py" ]; then echo "need Python 3.10+ for the Python run" >&2; exit 1; fi
    "$py" -m venv "$venv"
  fi
  "$venv/bin/pip" install --quiet -r "$suite/python/requirements.txt"
  "$venv/bin/python" "$suite/python/run.py" "$suite/cases.json" "$out/python.json"
}

rm -f "$out"/*.json
case "$target" in
  dotnet) run_dotnet ;;
  js) run_js ;;
  python) run_python ;;
  all) run_dotnet; run_js; run_python ;;
  *) echo "usage: $0 [check|record] [dotnet|js|python|all]" >&2; exit 2 ;;
esac

python3 "$suite/check.py" "$mode" "$out"
