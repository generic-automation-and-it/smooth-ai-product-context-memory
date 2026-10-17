# Write Path

How the session captures into the store at the authorized `--export` (or `--dryrun`) checkpoint. There
is no separate write agent: the session that runs the skill follows this procedure itself.

Run the write client, `context_memory_client.py`, on discrete candidate facts and target metadata.
Never a raw transcript. The write credential is never ambient: the client seeds only the read token and
base URL, so `CONTEXT_MEMORY_WRITE_TOKEN` is absent until you load it. Load it only at the authorized
checkpoint, with the deliberate write step the skill's `SKILL.md` names —
`set -a && source ~/.mimisbrunnr/credentials && set +a` (source the file `CONTEXT_MEMORY_CREDENTIAL_FILE`
names instead, when it is set — it is the file the clients read) — and **only inside a subshell around each
write-route invocation** (`preflight`, `set`, `resolve-group`, `upsert-initiative`, `update-group`,
`append-description`, `create-link`, `propose-label`, `ticket-parent`, `add-ticket-references` — the
`WRITE_ROUTES` set in
`context_memory_client.py`; `preflight` persists nothing but still authenticates write-only), so the
token never persists in the session's shell, where the read client would
then refuse to start:

```bash
( set -a && source ~/.mimisbrunnr/credentials && set +a && \
  python3 -B <skills-root>/_mimisbrunnr-shared/scripts/context_memory_client.py <preflight|set|resolve-group|upsert-initiative|update-group|append-description|create-link|propose-label|ticket-parent|add-ticket-references> ... )
# same subshell wrapper around each write-route invocation; never export the token into the session shell
```

The write client lives in the plugin's shared script folder
([HLD-010](https://github.com/generic-automation-and-it/smooth-ai-product-context-memory/tree/main/docs/hlds/010-skills-distribution/), LADR-01), so `<skills-root>` is
`.agents/skills`, `.claude/skills` or `.codex/skills` — whichever this tree exposes. Never name a peer
skill's folder to reach it.

Never earlier, and never print either credential. Write the batch and payload files below with the
file tool and nothing else.

## Pipeline

Before stage 1, turn the proposed group binding into a group — this is the only place a group or
initiative is created. On the create path run `upsert-initiative` first (a missing initiative
is a `404` from `resolve-group`), then `resolve-group`, and carry its `groupUuid` into every preflight
candidate and the `set` request. **After `resolve-group`, compare its `initiativeName` against the
proposed initiative: on mismatch stop the checkpoint** — tickets resolve first and the initiative is
create-path-only, so the reply names the initiative the write would actually land under. Offer the
caller the three honest options (drop the initiative and capture there; re-bind to tickets that
resolve nowhere for WET duplicates under the new initiative; `update-group` re-point to move the
group), and write nothing until it chooses. Under `--dryrun` run neither. Unless the session found a
`groupUuid` read-only (a ticket `query` returns one only when the group already holds memories) —
for a group that does not exist yet, or an existing group with no memories — report the group and
initiative as *would create* and preview the plan offline: `set --dryrun` needs a `groupUuid`, so say
the set stage was not tested rather than claiming a full dry run.

Run exactly once in fixed order:

1. **Preflight**: batched cross-group read-before-write; facts only, no API judgement.
2. **Redact**: scrub detected secrets before any content reaches storage; report rule names, field paths and replaced offsets only — never the replaced text. If a location covers prose rather than a secret, reword it and re-run. The scrubber recognises secret *shapes* only, so it is not a personal-data filter: personal data was already removed before any file was written (below).
3. **Dedupe / derive links**: judge subject matches and links from bounded recall.
4. **Atomicity check**: one memory per fact; split or skip bundled claims.
5. **Write**: one transactional `set`; API owns version ordering, identities, and graph writes.
6. **Held candidates**: a candidate held by atomicity, the value gate or staged dedup is appended to the
   review queue, and a pure create among them is captured separately as `Status = proposed` — its own
   preflight, its own `set --dryrun` veto, then its own `set` (`SKILL.md` → *A held candidate is also
   persisted*). An in-group match, a same-subject twin, a dedup duplicate or a cross-kind holder stays
   queue-file-only.

**Under `--product-name`, one more write follows stage 5 — and it is not a stage.** After the `set` has
committed, declare the product's graph edges through the same credential subshell:

- **Anchors**: every anchor the caller bound is declared — initiative (if bound) and repo (if bound),
  in that order. The *initiative name* comes from the resolved group when the kind is the initiative;
  the *repo name* stays what the caller bound. The two stay apart: a group that resolves under some
  initiative must not retarget a repository-bound capture. When both are bound both edges are declared
  — either alone attributes the memory (LADR-10), so a repo conflict (a repository has at most one
  owning product, so it answers `409` naming the owner, where the initiative edge can only `404`) is
  reported as a NOTE, not a silent skip — but it does not fail an export whose primary anchor already
  landed. No edge landed → not clean.
- **Verb**: `link-product-initiative` (`{"productName", "initiativeName", "reason", "source"}`) or
  `link-product-repo` (`{"productName", "repo", "reason", "source"}`). Both require a non-empty
  `reason`; the store sets `declaredAt`. Idempotent — an already-present pair answers `changed: false`.
- **Never create the product.** An unknown one is a `404` naming `upsert-product-name`: report that
  command rather than registering a name on the operator's behalf (LADR-10 — a typo must not create a
  registry entry).
- **The edge is not part of the `set` transaction.** A failure leaves the memories written; report it
  as a failed checkpoint step, never as a rollback and never as a clean checkpoint.
- **`--dryrun` declares nothing.** Report the edge as *would declare* from a read of the register
  (`product-names`) and run neither verb — a preview still needs no write credential.
- **Neither anchor bound**: refuse at the checkpoint, before `resolve-group` — not at Initialize, which
  never blocks a session — rather than after the memories have landed.

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

Finish with only bounded clarification needs followed by the digest: created, versioned, linked,
diverged, skipped(atomicity), skipped(duplicate-link), labels-proposed, authority applied, and
statuses. Recall rows used for dedup and link judgement are working material: reduce them to the
judgement, never paste them into the digest.
