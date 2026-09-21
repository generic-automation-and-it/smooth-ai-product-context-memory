#!/usr/bin/env node
// Pack and install the package in isolation, then execute every public CLI.
import { mkdirSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { spawnSync } from "node:child_process";

const __dirname = dirname(fileURLToPath(import.meta.url));
const pkgRoot = join(__dirname, "..");

const npm = process.platform === "win32" ? "npm.cmd" : "npm";
const tempRoot = mkdtempSync(join(tmpdir(), "mimisbrunnr-npm-smoke-"));
const packDir = join(tempRoot, "pack");
const installDir = join(tempRoot, "install");
mkdirSync(packDir);

const commands = [
  ["mimisbrunnr", ["context-memory", "--help"]],
  ["mimisbrunnr-context-memory", ["--help"]],
  ["mimisbrunnr-understanding", ["--help"]],
];

function run(command, args, options = {}) {
  const result = spawnSync(command, args, {
    cwd: pkgRoot,
    encoding: "utf8",
    ...options,
  });
  if (result.error) {
    throw new Error(`${command} failed to start: ${result.error.message}`);
  }
  if (result.status !== 0) {
    throw new Error(
      `${command} ${args.join(" ")} exited ${result.status}\n${result.stderr || result.stdout}`,
    );
  }
  return result;
}

try {
  const packed = run(npm, ["pack", "--json", "--pack-destination", packDir]);
  const [{ filename }] = JSON.parse(packed.stdout);
  const tarball = join(packDir, filename);

  run(npm, [
    "install",
    "--ignore-scripts",
    "--no-audit",
    "--no-fund",
    "--prefix",
    installDir,
    tarball,
  ]);

  const packageJson = JSON.parse(
    readFileSync(join(installDir, "node_modules", "@generic-automation-and-it", "mimisbrunnr-skills", "package.json")),
  );
  if (packageJson.name !== "@generic-automation-and-it/mimisbrunnr-skills") {
    throw new Error("installed package identity does not match");
  }

  for (const [name, args] of commands) {
    const executable = join(
      installDir,
      "node_modules",
      ".bin",
      process.platform === "win32" ? `${name}.cmd` : name,
    );
    run(executable, args, { cwd: installDir });
    console.log(`OK: ${name} ${args.join(" ")}`);
  }
} finally {
  rmSync(tempRoot, { recursive: true, force: true });
}

console.log("npm smoke passed.");
