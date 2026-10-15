#!/usr/bin/env python3
"""Compares what each JSONata implementation observed with the expectations in cases.json.

    check.py check  <observed-dir>   fail on any mismatch (the default use)
    check.py record <observed-dir>   rewrite the "expect" and "divergent" fields of cases.json from the
                                     observed results; review the diff before committing

<observed-dir> holds one JSON file per runtime (js.json, python.json, dotnet.json) as written by the runners.
Standard library only.

A case is "portable" when every implementation gives the same result: it carries one `expect`. Otherwise it
is "divergent" and carries one `expect` per runtime plus a `note`; each runtime must keep producing its
recorded result, so a library upgrade that changes behaviour (in either direction) shows up here.
"""
import json
import math
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
CASES = HERE / "cases.json"
RUNTIMES = ("js", "python", "dotnet")


def encode(v):
    """Non-finite floats are not JSON: write them as {"$nonFinite": "NaN"} (also "Infinity", "-Infinity")."""
    if isinstance(v, float) and not math.isfinite(v):
        return {"$nonFinite": "NaN" if math.isnan(v) else ("Infinity" if v > 0 else "-Infinity")}
    if isinstance(v, dict):
        return {k: encode(x) for k, x in v.items()}
    if isinstance(v, list):
        return [encode(x) for x in v]
    return v


def decode(v):
    if isinstance(v, dict):
        if list(v) == ["$nonFinite"]:
            return float(v["$nonFinite"])
        return {k: decode(x) for k, x in v.items()}
    if isinstance(v, list):
        return [decode(x) for x in v]
    return v


def same_value(a, b):
    """JSON equality where 2 == 2.0, NaN == NaN, and object key order counts."""
    if isinstance(a, bool) or isinstance(b, bool):
        return isinstance(a, bool) and isinstance(b, bool) and a == b
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return (math.isnan(a) and math.isnan(b)) or a == b
    if isinstance(a, dict) and isinstance(b, dict):
        return list(a) == list(b) and all(same_value(a[k], b[k]) for k in a)
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(same_value(x, y) for x, y in zip(a, b))
    return a == b


def same_result(a, b):
    """Result equality. Python reports null and undefined alike as kind "none", which matches either."""
    ka, kb = a["kind"], b["kind"]
    if "none" in (ka, kb):
        other = b if ka == "none" else a
        return other["kind"] in ("none", "undefined") or (other["kind"] == "value" and other["value"] is None)
    if ka != kb:
        return False
    if ka == "value":
        return same_value(a["value"], b["value"])
    if ka == "error":
        return a["code"] == b["code"]
    return True


def show(r):
    if r is None:
        return "(missing)"
    if r["kind"] == "value":
        return json.dumps(encode(r["value"]), ensure_ascii=False)
    if r["kind"] == "error":
        return "error " + r["code"]
    return r["kind"]


def load_observed(directory):
    observed = {}
    for rt in RUNTIMES:
        f = Path(directory) / (rt + ".json")
        if f.exists():
            observed[rt] = json.loads(f.read_text(encoding="utf-8"))
    return observed


def check(spec, observed):
    problems = []
    for c in spec["cases"]:
        expect = c.get("expect")
        for rt, obs in observed.items():
            got = obs["results"].get(c["id"])
            want = expect if not c.get("divergent") else (expect or {}).get(rt)
            if want is None:
                problems.append((c["id"], rt, "no expectation recorded", show(got)))
            elif got is None or not same_result(got, want):
                problems.append((c["id"], rt, show(want), show(got)))
    return problems


def record(spec, observed):
    missing = [rt for rt in RUNTIMES if rt not in observed]
    if missing:
        sys.exit("record needs every runtime; missing: " + ", ".join(missing))
    for c in spec["cases"]:
        per = {rt: observed[rt]["results"][c["id"]] for rt in RUNTIMES}
        first = per["js"]
        if all(same_result(first, per[rt]) for rt in RUNTIMES):
            # keep a real value over python's ambiguous "none"
            best = next((r for r in (per["js"], per["dotnet"], per["python"]) if r["kind"] != "none"), first)
            c["expect"] = best
            c.pop("divergent", None)
        else:
            c["divergent"] = True
            c["expect"] = per
            c.setdefault("note", "TODO: explain this divergence")
    spec["libraries"] = {rt: observed[rt]["library"] for rt in RUNTIMES}
    CASES.write_text(json.dumps(encode(spec), indent=1, ensure_ascii=False, allow_nan=False) + "\n", encoding="utf-8")


def main():
    if len(sys.argv) != 3 or sys.argv[1] not in ("check", "record"):
        sys.exit(__doc__)
    mode, directory = sys.argv[1], sys.argv[2]
    spec = decode(json.loads(CASES.read_text(encoding="utf-8")))
    observed = load_observed(directory)
    if not observed:
        sys.exit("no observed results in " + directory)
    if mode == "record":
        record(spec, observed)
        div = sum(1 for c in spec["cases"] if c.get("divergent"))
        print(f"recorded {len(spec['cases'])} cases: {len(spec['cases']) - div} portable, {div} divergent")
        return
    problems = check(spec, observed)
    ran = ", ".join(f"{rt} ({o['library']})" for rt, o in observed.items())
    total = len(spec["cases"]) * len(observed)
    print(f"ran {ran}")
    for cid, rt, want, got in problems:
        print(f"  MISMATCH {cid} [{rt}]\n    expected: {want}\n    observed: {got}")
    print(f"{total - len(problems)}/{total} case results as expected")
    sys.exit(1 if problems else 0)


main()
