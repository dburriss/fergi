#!/usr/bin/env bash
# Checks docs/mapping.schema.json against the real reader (Mapping.ofParsed): every file Mapping.toNode writes
# and every case in tests/mapping-schema/cases must get the same verdict from both.
#
#   scripts/mapping-schema.sh
#
# Prerequisites: .NET SDK 8+, Python 3.10+ (3.13 tested; the venv is tests/mapping-schema/python/.venv, and the
# first run needs network for `pip install`). Exit code is non-zero on any disagreement.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
suite="$root/tests/mapping-schema"
out="$suite/observed"
rm -rf "$out"
mkdir -p "$out"

dotnet run --project "$suite/dotnet/Runner.fsproj" -c Release -- "$suite/cases" "$out"

venv="$suite/python/.venv"
if [ ! -x "$venv/bin/python" ]; then
  py="$(command -v python3.13 || command -v python3.12 || command -v python3.11 || command -v python3.10 || true)"
  if [ -z "$py" ]; then echo "need Python 3.10+" >&2; exit 1; fi
  "$py" -m venv "$venv"
fi
"$venv/bin/pip" install --quiet -r "$suite/python/requirements.txt"
"$venv/bin/python" "$suite/python/check.py" "$root/docs/mapping.schema.json" "$suite/cases" "$out"
