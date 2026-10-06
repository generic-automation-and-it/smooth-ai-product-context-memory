---
name: memory-write
description: Delegated Mimisbrunnr capture pipeline with write capability.
tools:
  - Bash
  - Write
---

# Memory Write

Run the write client, `context_memory_client.py`. Receive discrete candidate facts and target metadata.
Never accept a raw transcript. The write credential is never ambient: the client seeds only the read
token and base URL, so `CONTEXT_MEMORY_WRITE_TOKEN` is absent until you load it. Load it only at the
authorized `--export` (or `--dryrun`) checkpoint, in this worker's shell, with the deliberate write step
the skill's `AGENTS.md` names — `set -a && source ~/.mimisbrunnr/credentials && set +a` — and never
earlier. Never print either credential. The `Write` tool is for the batch and payload files below and
nothing else.

## Pipeline

Before stage 1, turn the main thread's proposed group binding into a group — this is the only place a
group or initiative is created. On the create path run `upsert-initiative` first (a missing initiative
is a `404` from `resolve-group`), then `resolve-group`, and carry its `groupUuid` into every preflight
candidate and the `set` request. Under `--dryrun` run neither: for a group that does not exist yet,
report the group and initiative as *would create* and preview the plan offline — `set --dryrun` needs a
`groupUuid`, so say the set stage was not tested rather than claiming a full dry run.

Run exactly once in fixed order:

1. **Preflight**: batched cross-group read-before-write; facts only, no API judgement.
2. **Redact**: scrub detected secrets before any content reaches storage; report rule names, field paths and replaced offsets only — never the replaced text. If a location covers prose rather than a secret, reword it and re-run. The scrubber recognises secret *shapes* only, so it is not a personal-data filter: personal data was already removed before any file was written (below).
3. **Dedupe / derive links**: judge subject matches and links from bounded recall.
4. **Atomicity check**: one memory per fact; split or skip bundled claims.
5. **Write**: one transactional `set`; API owns version ordering, identities, and graph writes.

**Before creating any batch or payload file, mask and generalise every personal identifier** — any
personal data within the meaning of the GDPR (information relating to an identified or identifiable
person: names, contact details, identity numbers, employee, account or online identifiers, home-folder
paths, location data, special-category data). Drop the value; generalise a person to a role (*"the
user"*) and a value to its type (*"an identity number"*), until no one can be singled out. A fact that
means nothing once generalised is not captured; report it in the digest. **Mask every secret you
recognise the same way** — credentials, tokens, keys, passwords, connection strings become `<REDACTED>`
— so nothing written holds a value that has not passed both steps; the secret scrubber is the mechanical
second check on the file, and cleanup is not a step at all. See SKILL.md for the full rule.

Every batch and payload file goes under `.context/mimisbrunnr-scratch/`, written with the file tool —
never an `echo`/heredoc, and never an environment variable (a child process inherits it and the process
table exposes it). Create the folder owner-only first (`mkdir -p -m 700 .context/mimisbrunnr-scratch`;
the readers refuse a batch other accounts can read); if it already exists, `chmod 700` it — or remove
and recreate it — before writing any file, since `mkdir -m` does not change an existing folder. Then
write `.context/mimisbrunnr-scratch/.gitignore`
holding `*`, before any batch file, so the folder ignores itself even in a checkout that does not ignore
`.context/`. Pass `--consume` to `redact.py`, `atomicity.py` and every `--payload` so each file is
deleted once read — **except `set --dryrun`**, whose payload file is kept and passed unchanged to the
real `set --payload <file> --consume`. Remove `.context/mimisbrunnr-scratch/` after the real write, and
also when the checkpoint is refused, fails or is abandoned: the batch file is the not-yet-secret-redacted
copy of the candidates (it holds no personal data), and `decisions_gate.py score < <batch-file>` cannot
consume its own input, so the folder cleanup is what removes that copy.
Assign `createUuid` to every create before dry-run. For an existing group, reuse the identical payload
file — same UUIDs, same items — for the real write; never rebuild it. For a group created at this
checkpoint, build and validate the `set` payload after creation supplies its `groupUuid`.
Never use follow-up `create-link` for a link known at capture time.

Apply BR-10 authority only after confirming same subject, scope, applicability, and business time:
verified shipped behaviour beats specification; agreed vocabulary beats alternatives. Otherwise retain
both claims and use `divergence.py` to add a proposed divergence plus two `contradicts` links. Never use
a divergence record as conflict evidence. Before composition, query current proposed `kind: divergence`
records with sources and pass them as `existingDivergences`; existing unordered exact-claim pair means
no new divergence. Also probe deterministic divergence UUID by exact identity before write; bounded
kind recall alone is insufficient after 200 open conflicts.

For a rule-resolvable disagreement, use `authority.py`. If candidate wins, one version bump makes it
current and retains existing loser in history. If existing wins, two ordered version writes first record
candidate loser, then restore existing winner as current; API accepts ordered repeated targets in one
transaction. Report authority and losing-position retention in digest.

Return only bounded clarification needs followed by digest: created, versioned, linked, diverged,
skipped(atomicity), skipped(duplicate-link), labels-proposed, authority applied, and statuses. Raw recall
rows never leave this delegated context.
