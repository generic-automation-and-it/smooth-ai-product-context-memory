#!/usr/bin/env node
// Package smoke test — verify the shipped python clients resolve and execute
// --help without importing the whole mimisbrunnr runtime.
import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { spawnSync } from "node:child_process";

const __dirname = dirname(fileURLToPath(import.meta.url));
const pkgRoot = join(__dirname, "..");

const clients = [
  ".agents/skills/mimisbrunnr-context-memory/scripts/context_memory_client.py",
  ".agents/skills/mimisbrunnr-understanding/scripts/understanding_client.py",
];

let ok = true;
for (const rel of clients) {
  const script = join(pkgRoot, rel);
  if (!existsSync(script)) {
    console.error(`FAIL: missing ${rel}`);
    ok = false;
    continue;
  }
  const res = spawnSync("python3", [script, "--help"], { encoding: "utf8" });
  if (res.error) {
    console.error(`FAIL: python3 not runnable — ${res.error.message}`);
    ok = false;
    continue;
  }
  if (res.status !== 0) {
    console.error(`FAIL: ${rel} --help exited ${res.status}`);
    ok = false;
    continue;
  }
  console.log(`OK: ${rel} --help`);
}

if (!ok) process.exit(1);
console.log("npm smoke passed.");
