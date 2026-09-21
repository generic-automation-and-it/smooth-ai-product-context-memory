#!/usr/bin/env node
// Shared launcher for the Mímisbrunnr python clients.
// Each bin resolves the packaged python script relative to this module and
// spawns `python3 <script> <args...>`, forwarding cwd, env and exit code.
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { existsSync } from "node:fs";

const __dirname = dirname(fileURLToPath(import.meta.url));

export function runClient(relScript) {
  const script = join(__dirname, "..", relScript);
  if (!existsSync(script)) {
    console.error(`mimisbrunnr: script not found: ${script}`);
    process.exit(2);
  }
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
