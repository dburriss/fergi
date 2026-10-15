// Runs every case in cases.json with the reference jsonata (npm) and writes what it observed.
//   <runtime> run.mjs <cases.json> <observed.json>
import jsonata from "jsonata";
import fs from "fs";
import { createRequire } from "module";

const [casesFile, outFile] = process.argv.slice(2);
const { guardrails, cases } = JSON.parse(fs.readFileSync(casesFile, "utf8"));
const version = createRequire(import.meta.url)("jsonata/package.json").version;

const results = {};
for (const c of cases) {
  try {
    const expr = jsonata(c.expr, { timeout: guardrails.timeoutMs, stack: guardrails.maxDepth });
    const r = await expr.evaluate(c.ctx);
    results[c.id] = r === undefined ? { kind: "undefined" } : { kind: "value", value: JSON.parse(JSON.stringify(r)) };
  } catch (e) {
    results[c.id] = { kind: "error", code: e.code ?? e.name ?? "Error" };
  }
}
fs.writeFileSync(outFile, JSON.stringify({ runtime: "js", library: "jsonata " + version, results }, null, 1));
