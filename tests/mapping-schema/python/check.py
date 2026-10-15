"""Compares the JSON Schema with Mapping.ofParsed over every case file and every file Mapping.toNode wrote.

    check.py <schema> <cases dir> <observed dir>

valid/ and generated/ files must be accepted by both; invalid/ files must be rejected by both. A file the two
disagree on is a bug in the schema or in the reader. Exit code is non-zero on any problem.
"""
import json
import sys
from pathlib import Path

from jsonschema import Draft202012Validator

schema_path, cases, observed = (Path(a) for a in sys.argv[1:4])
schema = json.loads(schema_path.read_text())
Draft202012Validator.check_schema(schema)
validator = Draft202012Validator(schema)
verdicts = json.loads((observed / "dotnet.json").read_text())

problems = []
for rel, verdict in verdicts.items():
    root = cases if (cases / rel).exists() else observed
    instance = json.loads((root / rel).read_text())
    schema_errors = sorted(validator.iter_errors(instance), key=lambda e: list(e.absolute_path))
    schema_ok = not schema_errors
    reader_ok = verdict["accepted"]
    expected = not rel.startswith("invalid/")
    status = "ok"
    if schema_ok != reader_ok:
        status = "DISAGREE"
        problems.append(f"{rel}: schema {'accepts' if schema_ok else 'rejects'}, ofParsed {'accepts' if reader_ok else 'rejects'}"
                        + (f" ({verdict['errors'][0]})" if verdict["errors"] else "")
                        + (f" ({schema_errors[0].message[:120]})" if schema_errors else ""))
    elif reader_ok != expected:
        status = "UNEXPECTED"
        problems.append(f"{rel}: both {'accept' if reader_ok else 'reject'} it, but it is in {rel.split('/')[0]}/")
    print(f"{status:10} {rel}")

print(f"\n{len(verdicts)} files checked, {len(problems)} problems")
for p in problems:
    print("  " + p)
sys.exit(1 if problems else 0)
