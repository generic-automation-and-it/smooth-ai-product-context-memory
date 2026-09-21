#!/usr/bin/env node
// mimisbrunnr — dispatcher. Route the first arg to the matching client; a bare
// invocation lists the available commands.
import { runClient } from "./_run.js";

const COMMANDS = {
  "context-memory": ".agents/skills/mimisbrunnr-context-memory/scripts/context_memory_client.py",
  "understanding": ".agents/skills/mimisbrunnr-understanding/scripts/understanding_client.py",
};

const [cmd, ...rest] = process.argv.slice(2);
if (cmd && COMMANDS[cmd]) {
  process.argv = [process.argv[0], process.argv[1], ...rest];
  runClient(COMMANDS[cmd]);
} else {
  console.log("mimisbrunnr — persistent AI context-memory clients");
  console.log("");
  console.log("Usage:");
  console.log("  mimisbrunnr <command> [args...]");
  console.log("");
  console.log("Commands:");
  for (const [name] of Object.entries(COMMANDS)) {
    console.log(`  ${name}`);
  }
  console.log("");
  console.log("Also available as dedicated bins:");
  console.log("  mimisbrunnr-context-memory   (get/set durable context)");
  console.log("  mimisbrunnr-understanding    (load/import/session-dump)");
  process.exit(cmd ? 1 : 0);
}
