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

Run exactly once in fixed order:

1. **Preflight**: batched cross-group read-before-write; facts only, no API judgement.
2. **Redact**: scrub detected secrets before any content reaches storage; report rule names, field paths and replaced offsets only — never the replaced text. If a location covers prose rather than a secret, reword it and re-run.
3. **Dedupe / derive links**: judge subject matches and links from bounded recall.
4. **Atomicity check**: one memory per fact; split or skip bundled claims.
5. **Write**: one transactional `set`; API owns version ordering, identities, and graph writes.

Assign `createUuid` to every create before dry-run. Reuse identical UUIDs and payload for real write.
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
