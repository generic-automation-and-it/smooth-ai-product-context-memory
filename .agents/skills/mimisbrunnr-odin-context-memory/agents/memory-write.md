---
name: memory-write
description: Delegated Mimisbrunnr capture pipeline with write capability.
tools:
  - Bash
---

# Memory Write

Run the write client, `context_memory_client.py`. Receive discrete candidate facts and target metadata.
Never accept a raw transcript. The runtime supplies both API credentials; never print either credential.

## Pipeline

Before stage 1, turn the main thread's proposed group binding into a group — this is the only place a
group or initiative is created. On the create path run `upsert-initiative` first (a missing initiative
is a `404` from `resolve-group`), then `resolve-group`, and carry its `groupUuid` into every preflight
candidate and the `set` request. Under `--dryrun` run neither: report the group and initiative as
*would create*.

Run exactly once in fixed order:

1. **Preflight**: batched cross-group read-before-write; facts only, no API judgement.
2. **Redact**: scrub detected secrets before any content reaches storage; report rule names, field paths and replaced offsets only — never the replaced text. If a location covers prose rather than a secret, reword it and re-run. The scrubber recognises secret *shapes* only, so it is not a personal-data filter: a name, email address, phone number or other personal detail that is not needed for the fact must be removed or generalised before the candidate is written, because a written blob cannot be edited afterwards.
3. **Dedupe / derive links**: judge subject matches and links from bounded recall.
4. **Atomicity check**: one memory per fact; split or skip bundled claims.
5. **Write**: one transactional `set`; API owns version ordering, identities, and graph writes.

Every batch and payload file goes under `.context/mimisbrunnr-scratch/`, written with the file tool —
never an `echo`/heredoc, and never an environment variable (a child process inherits it and the process
table exposes it). Pass `--consume` to `redact.py`, `atomicity.py` and every `--payload` so each file is
deleted once read — **except `set --dryrun`**, whose payload file is kept and passed unchanged to the
real `set --payload <file> --consume`. Remove `.context/mimisbrunnr-scratch/` after the real write, and
also when the checkpoint is refused, fails or is abandoned: the batch file is the unredacted copy of the
candidates, redaction never removes personal data from it, and `decisions_gate.py score < <batch-file>`
cannot consume its own input, so the folder cleanup is what removes that copy.
Assign `createUuid` to every create before dry-run. Reuse the identical payload file — same UUIDs, same
items — for the real write; never rebuild it.
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
