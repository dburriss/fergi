"""Runs every case in cases.json with jsonata-python and writes what it observed.

    python run.py <cases.json> <observed.json>

jsonata-python returns None for both an undefined result and a null result, so those are recorded as
kind "none" and check.py accepts either expectation for them.
"""
import json
import sys
from importlib.metadata import version

import jsonata
from jsonata.utils import Utils


def plain(v):
    if v is Utils.NULL_VALUE:
        return None
    if isinstance(v, dict):
        return {k: plain(x) for k, x in v.items()}
    if isinstance(v, (list, tuple)):
        return [plain(x) for x in v]
    return v


cases_file, out_file = sys.argv[1:3]
spec = json.load(open(cases_file, encoding="utf-8"))
g = spec["guardrails"]

results = {}
for c in spec["cases"]:
    try:
        expr = jsonata.Jsonata(c["expr"])
        frame = expr.create_frame()
        frame.set_runtime_bounds(g["timeoutMs"], g["maxDepth"])
        r = expr.evaluate(c["ctx"], frame)
        results[c["id"]] = {"kind": "none"} if r is None else {"kind": "value", "value": plain(r)}
    except Exception as e:  # noqa: BLE001 - every failure is a recorded result
        code = getattr(e, "error", None)
        results[c["id"]] = {"kind": "error", "code": code if isinstance(code, str) else type(e).__name__}

json.dump(
    {"runtime": "python", "library": "jsonata-python " + version("jsonata-python"), "results": results},
    open(out_file, "w", encoding="utf-8"),
    indent=1,
    ensure_ascii=False,
)
