#!/usr/bin/env bash
# The `fergi` command line over the example data: validate, run, input lists and a dry run.
#
#   dotnet build Fergi.slnx -c Release
#   examples/cli.sh
#
# Uses the `fergi` tool when it is installed (dotnet tool install --global Fergi.Tool --prerelease),
# else the build output. Output files go to a temporary folder, not into the repo.
# Reference: docs/reference/cli.md, docs/how-to/use-the-cli.md

set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

if command -v fergi >/dev/null 2>&1; then
  fergi() { command fergi "$@"; }
else
  fergi() { dotnet "$root/src/Fergi.Cli/bin/Release/net8.0/Fergi.Cli.dll" "$@"; }
fi

out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT
data=examples/data

step() { printf '\n$ %s\n' "$*"; }

step "fergi --version"
fergi --version

# Check a mapping file on its own: no inputs are read.
step "fergi validate $data/mappings/deploy.yaml"
fergi validate "$data/mappings/deploy.yaml"

# Errors go to stderr as file:line:col: code: message, and the exit code is 1.
step "fergi validate $data/mappings/broken.yaml   (fails on purpose)"
fergi validate "$data/mappings/broken.yaml" || echo "exit code $?"

# -i binds an input. Without name= the name is the file name without its extension (org.yaml binds "org").
step "fergi run deploy.yaml -i org.yaml -i service=billing.yaml -o out/billing.yaml"
fergi run "$data/mappings/deploy.yaml" -i "$data/org.yaml" -i service="$data/billing.yaml" -o "$out/billing.yaml"
cat "$out/billing.yaml"

# The output format follows the extension, and inputs may mix formats.
step "the same mapping, a JSON service file and JSON output"
fergi run "$data/mappings/deploy.yaml" -i "$data/org.yaml" -i service="$data/payments.json" -o "$out/payments.json"
cat "$out/payments.json"

# -l adds files to an input list. A value with * or ? is expanded by the CLI, sorted by path.
step "fergi run catalog.yaml -l 'services=$data/services/*.yaml' -o out/catalog.yaml"
fergi run "$data/mappings/catalog.yaml" -l "services=$data/services/*.yaml" -o "$out/catalog.yaml"
cat "$out/catalog.yaml"

# --dry-run prints the changes against the current output and writes nothing.
step "fergi run ... -o current-output.yaml --dry-run"
cp "$data/current-output.yaml" "$out/current.yaml"
fergi run "$data/mappings/deploy.yaml" -i "$data/org.yaml" -i service="$data/billing.yaml" -o "$out/current.yaml" --dry-run
diff -q "$data/current-output.yaml" "$out/current.yaml" && echo "(output file untouched)"
