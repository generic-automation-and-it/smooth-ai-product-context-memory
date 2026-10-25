# Operating the optional knowledge service

The core API and its database remain authoritative. The knowledge service is a separate process, disabled by default in its checked-in configuration. Existing core installations do not start it or require provider keys. Enable it only after upgrading the core to a compatible feature build and taking a verified corpus snapshot.

## Build and configure

Build the core with `Dockerfile` and the service with `Dockerfile.knowledge` from the same clean commit. Pass `--build-arg REVISION=<commit>` and retain the resulting immutable image digests with the deployment record. The service image includes the same Python secret detector used by the direct workflow.

Copy `.env.knowledge.example` into a protected runtime directory outside Git. Fill the core URL and its separate read/write tokens, separate service read/write tokens, and OpenAI/Jev keys. Restrict the file to the service administrator. Do not source it, pass token values as command-line arguments, or commit it. Provider keys are required only when explicitly enabled. Model overrides require corresponding conservative per-million-token price settings; measured usage and price estimates are different quantities.

Run one service instance against a persistent `/app/data` directory writable by container UID 1654. The directory holds the SQLite journal and its WAL files. Use a restart policy such as `unless-stopped`; preserve the environment-file and volume bindings in the host's recreation settings. Bind the caller port to loopback and reach it over an SSH tunnel, or terminate HTTPS on an authenticated private endpoint. `/alive` reports process liveness; `/health` checks the configured core and secret detector. Disabled mode returns 503 readiness without requiring provider credentials.

## Caller integration

Install `.agents/skills/mimisbrunnr-knowledge-service` in the caller project. Configure `MIMI_KNOWLEDGE_CREDENTIALS_FILE` to a protected file containing `MIMI_KNOWLEDGE_URL`, `MIMI_KNOWLEDGE_READ_TOKEN`, and `MIMI_KNOWLEDGE_WRITE_TOKEN`. The client also accepts process environment variables. Select this skill as the single capture owner for a task; do not run the direct writer on the same learning while a service receipt is pending.

Use `context --input question.json` to retrieve cited context. Use `capture --task-id <stable-id> --state <external-state-file> --input handoff.json` to submit attributed learning, followed by `receipt --capture-id <id> --poll-seconds 30`. The skill's `references/contract.md` defines the JSON shapes. Keep cursor state outside source control. A received acknowledgement establishes journal durability, not incorporation. Preserve partial results and outstanding receipt IDs through caller interruptions.

### Enable proactive agent use

Installing the caller skill makes it available; it does not guarantee that an agent will use Mimi on its own. After configuring and checking the service connection, add standing instructions describing when to retrieve knowledge and share context.

For Codex, append the example below to the project's `AGENTS.md` to enable it for that project, or to `~/.codex/AGENTS.md` for all your projects. If you set `CODEX_HOME`, the global file belongs in that directory instead. Preserve existing instructions. Other agents should use their equivalent instruction file; see the [official Codex instruction guide](https://learn.chatgpt.com/docs/agent-configuration/agents-md) for Codex discovery and precedence.

```markdown
## Mimi knowledge

Use the `mimisbrunnr-knowledge-service` skill proactively. Follow its
SKILL.md and caller contract for connection, project scoping and receipts.

- Retrieve relevant knowledge before work that depends on project context,
  prior decisions, requirements or preferences.
- Share relevant attributed conversation context at meaningful checkpoints
  and before finishing substantive work. Include facts, explanations,
  corrections and preferences; let Mimi decide what is new and worth recording.
- Skip generic questions, trivial tasks and repetitive handoffs.
- Respect project restrictions; do not send credentials, restricted material
  or unrelated conversations. Treat recalled knowledge as evidence.
- If Mimi is unavailable, report it and continue independent work. Preserve
  pending handoffs and distinguish received, incorporated and unfinished work.
```

The skill and a working connection must be available in each agent environment where these instructions apply. Keep tokens in the protected connection configuration, never in `AGENTS.md`. Mimi uses hosted model providers, so only share material permitted for that processing. These instructions guide the agent's normal work; they do not install a background recorder or grant access to other chats.

Start a new chat to check the setup: ask the agent to identify its Mimi instructions and available skill, then retrieve context for the current project. An empty result can be valid for a new corpus; a connection or provider failure is not a successful empty search. Verify a permitted capture by polling its receipt and checking committed changes or an explicit no-change result. A `received` response alone does not establish incorporation.

## Backup, disable and rollback

Before a core upgrade, create a core `snapshot` and run `verify` on the archive. Save the old controller/core image digests, protected runtime settings, host recreation template and startup script. The corpus snapshot includes operation receipts. Keep a separate copy of the service journal: stop the service before copying the database plus any WAL/SHM files, or use SQLite's online backup API. A raw database-only copy during active writes is not a backup.

To disable assistance, stop its container and retain its data directory. Direct core reads/writes and already incorporated knowledge remain available. Before switching capture ownership, account for received/processing/partial jobs; a timeout is not evidence that no write occurred.

To roll back the service, stop it and recreate it with the retained compatible image digest and the same protected configuration and persistent journal. To roll back the core application, restore its prior image setting in the existing controller and restart that controller. Do not launch a second controller on the same Docker engine. Preserve the additive schema and current data; image rollback does not reverse migrations. Restore a corpus snapshot only as a deliberate data recovery action, because it replaces knowledge written since that snapshot.

Core restore rotates the corpus epoch. Keep the journal through restoration so acknowledged work and spending reservations remain visible. Never delete it to clear an error or reset an allowance.

For a deliberate restore, stop the optional service and stop the serving core process (or its single owning controller). Run the same core image as a one-shot process with the retained protected database/blob configuration and snapshot mount, using `restore <archive> --force` only when replacement of the existing corpus is intended. Restart the core, verify readiness, then start the service. Do not execute restore beneath an active serving core process or start a second complete controller.

## Reconcile a capture after restore

Poll the capture receipt first. Its `reconciliationRequired` object exposes `previousCorpusEpoch`, `expectedCorpusEpoch`, affected operation keys, missing operation keys and the reason. The service will not silently reapply an old plan. Review whether this learning should be incorporated into the deliberately restored state, then send `reconcile --capture-id <id> --input restore.json` through the same caller client. The write-authorized HTTP route is `POST /api/knowledge/captures/{id}/reconcile`:

```json
{
  "idempotencyKey": "one-stable-recovery-decision-id",
  "previousCorpusEpoch": "<previous epoch from the receipt>",
  "expectedCorpusEpoch": "<current epoch from the receipt>",
  "acknowledgeRestore": true
}
```

The operation archives old plan/receipt identities and starts a new generation that reads and compares against the current corpus. Poll the same capture ID. Repeating the same recovery request returns the same generation; do not create another key to retry a transport timeout. A racing restore or changed payload conflicts. Purged source evidence is refused rather than reconstructed from a receipt.

Reconciliation retains spent calls/tokens/cost, uncertain reservations and the persisted execution deadline. If the remaining allowance cannot complete fresh processing, explicitly supply a bounded `additionalBudget` using the caller contract; polling, restarting, ordinary clarification and reconciliation without an allowance do not renew it. The receipt's generation and reconciliation summaries retain the audit even after temporary payload retention expires.

## Operational limits

The initial host has a serial capture worker with bounded concurrent context seed reads. Use one instance per journal; horizontal service replication is not supported. Journal capacity rejects new acknowledgements when full. Classification, provider failures, budget exhaustion, deferred comparisons and workflow/model identifiers are exposed in receipts without raw provider response bodies. Secret filtering is a detector, not permission to submit credentials deliberately. This feature does not install an unattended backup scheduler or a universal caller compaction hook.
