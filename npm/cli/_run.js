#!/usr/bin/env node
// Shared launcher for the Mímisbrunnr python clients.
// Each bin resolves the packaged python script relative to this module and
// spawns `python3 <script> <args...>`, forwarding cwd, env and exit code.
import { spawn, spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { existsSync } from "node:fs";

const __dirname = dirname(fileURLToPath(import.meta.url));

// The floor, checked rather than documented. `spawn("python3", ...)` resolves whatever `python3`
// happens to be first on PATH, which on several distributions is 3.8 — older than anything these
// scripts support. The failure that produces is a SyntaxError or an AttributeError from deep
// inside a client, naming a line that has nothing to do with the version. A version check turns that
// into one sentence at the point of invocation.
//
// 3.9 is the floor, not 3.11, and the reason is the sub-second fraction. `fromisoformat` only
// accepts an arbitrary number of fractional digits from 3.11; earlier versions accept 3 or 6, and
// `System.Text.Json` emits a 7-digit tick count or a trailing-zero-trimmed fraction. The clients
// normalise the fraction before parsing, so both older versions work — checked once here so a
// genuinely older interpreter is still refused rather than failing obscurely later.
const MIN_PYTHON = [3, 9];

function checkPythonFloor() {
  const probe = spawnSync("python3", ["-c", "import sys;print('%d %d' % sys.version_info[:2])"], {
    encoding: "utf8",
  });
  if (probe.error || probe.status !== 0) {
    console.error(
      "mimisbrunnr: could not determine the python3 version. Install Python 3.9 or newer, " +
        "or put a suitable python3 first on PATH."
    );
    process.exit(2);
  }
  const [major, minor] = (probe.stdout || "").trim().split(/\s+/).map(Number);
  if (!Number.isInteger(major) || major < MIN_PYTHON[0] ||
      (major === MIN_PYTHON[0] && minor < MIN_PYTHON[1])) {
    console.error(
      `mimisbrunnr: python3 is ${major}.${minor}; ${MIN_PYTHON.join(".")} or newer is required. ` +
        "Install a newer Python, or put a suitable python3 first on PATH."
    );
    process.exit(2);
  }
}

export function runClient(relScript) {
  const script = join(__dirname, "..", "..", relScript);
  if (!existsSync(script)) {
    console.error(`mimisbrunnr: script not found: ${script}`);
    process.exit(2);
  }
  checkPythonFloor();
  const proc = spawn("python3", [script, ...process.argv.slice(2)], {
    stdio: "inherit",
    env: process.env,
  });
  proc.on("error", (err) => {
    console.error(`mimisbrunnr: failed to spawn python3: ${err.message}`);
    process.exit(2);
  });
  proc.on("exit", (code, signal) => {
    if (signal) process.kill(process.pid, signal);
    else process.exit(code ?? 1);
  });
}
