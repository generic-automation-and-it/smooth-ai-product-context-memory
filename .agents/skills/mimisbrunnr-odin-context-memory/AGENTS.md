# AGENTS.md - mimisbrunnr-odin-context-memory

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — the five-stage write pipeline, modes, switches and the path/credential table live there; scripts are in `_mimisbrunnr-shared/scripts/`.

## Non-Negotiables

- **SKILL.md restates the atomicity and cross-group subject-matching rules at three points on purpose** —
  Listen, the pre-write round and the pipeline table. Don't deduplicate them: trials showed the model
  bundling compound records again once a restatement was removed.
- **Recall is cross-group; a version bump is not.** Subject recall reads across groups so a same-subject
  memory elsewhere is seen; only a match inside the request's `groupUuid` may be bumped, and a cross-group
  match becomes a new memory plus a typed link. Don't widen the version-target lookup to "fix" an
  unqualified "deduplication is cross-group" wording — it reintroduces cross-group lock ordering.
- **Env vars and who receives them.** `context_memory_read_client.py` reads only `CONTEXT_MEMORY_READ_TOKEN`
  and refuses to start if any write-token spelling is set. `context_memory_client.py` reads
  `CONTEXT_MEMORY_WRITE_TOKEN` for write routes only, loaded inside a subshell around each write-route
  invocation. `decisions_gate.py` reads `CONTEXT_MEMORY_DECISIONS_*` and sends
  `CONTEXT_MEMORY_DECISIONS_API_KEY` only as its bearer header. `CONTEXT_MEMORY_BASE_URL` is not a secret.

## Architecture Decisions

### LADR-002 — Link derivation shares the pre-write round with dedup

- **Status:** Accepted · **Date:** 2026-09
- **Decision:** link derivation and cross-group dedup need the same bounded semantic recall; preflight is
  array-in/array-out so it sees intra-batch collisions (two candidates sharing a subject). Derived links are
  reported in the digest, never derived silently.
- **Consequence:** `set` writes links in the same transaction, so a plain-`set` digest is a receipt, not a
  veto — `--dryrun` is the only pre-write veto. Never reword it as "surfaced for veto".

### LADR-003 — Redaction is redact-and-flag, not reject

- **Status:** Accepted · **Date:** 2026-09
- **Decision:** fingerprint-detect and scrub the span, record the rule, field and offsets in the digest;
  content is never logged.
- **Consequence:** a secret no rule recognises is stored as written. Describe the gate as "recognisable
  secrets are gated", never "sensitive material never reaches storage".

### LADR-005 — Separate read and write execution contexts — superseded by LADR-008

- **Status:** Superseded by LADR-008 · **Date:** 2026-09
- **Decision:** delegate recall and capture to two agents with a read-only client and API-enforced
  read/write credentials.
- **Consequence:** the read client's refusal and the store-side capability check outlived it.

### LADR-007 — The capture batch is written to disk before redaction, and that copy is bounded

- **Status:** Accepted · **Date:** 2026-10
- **Decision:** the redactor needs the unredacted text as input, and an agent can hand a script text through a
  file, argv or the environment — so a file under `.context/mimisbrunnr-scratch/`, with every exposure of it
  bounded: owner-only folder, self-ignoring `.gitignore`, `--consume` on read, folder removed after the real
  write. Personal data and recognised secrets are masked before the file is written.
- **Consequence:** a review re-raising "unredacted content on disk" against a new call site checks that site
  against these bounds — a site without them is the defect, the channel is not. Rejected: argv or a heredoc
  (process table, shell history, quotes rewrite the command); an environment variable (every child inherits
  it); a folder outside the checkout (the file tool may not write there).

### LADR-008 — No read or write subagents

- **Status:** Accepted — supersedes LADR-005 · **Date:** 2026-10-14
- **Decision:** the session runs `references/read-path.md` and `references/write-path.md` itself; the write
  credential is loaded only inside a subshell around each write-route invocation. The registrations worked in
  one harness only and pinned a model against the repository's effort rule.
- **Consequence:** one behaviour in every harness. Accepted cost: raw recall rows pass through the session's
  context (bounded by the output shapes and the 200-row recall cap). Rejected: Claude registrations plus
  OpenCode copies (a third format to keep in step); keeping them with `effort:` (fixes the pin, not the gap).

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-10-15 | Ralph loop: four fixes in the shared capture client and queue, verified by the odin and queue suites | — |
| 2026-09-10 | Five-stage pipeline with preflight first | — |
| 2026-09-10 | Contract-coherence pass | — |
| 2026-09-12 | Requirements recorded | #17 |
| 2026-09-12 | Skill implemented (client, redactor, 20-candidate cap) | #17 |
| 2026-09-12 | Skill implemented (no C#): Added `scripts/context_memory_client.py` (14 subcommands, base-URL + health probe, `MAX_CANDIDATES`=20 cap… | #17 |
| 2026-09-13 | Unknown request fields are a 400, so a misspelling never defaults silently | — |
| 2026-09-13 | Facet/tag recall matches ANY by default | — |
| 2026-09-13 | The API rejects unknown request fields as a 400, so a misspelled payload field… | — |
| 2026-09-13 | `/query` recall | — |
| 2026-09-14 | Strict expected-parent-before-no-op | — |
| 2026-09-14 | `ticket-parent` PUT and bounded `ticket-paths` added | — |
| 2026-09-14 | `## Request Bodies` documented | — |
| 2026-09-14 | Added exact-identity `ticket-parent` PUT with explicit nullable parent/expectedParent, source metadata and no-network local dry-run | — |
| 2026-09-14 | SKILL.md gained `## Request Bodies` | — |
| 2026-09-15 | Near-miss query frozen to `tags` + `facetMatchMode` | — |
| 2026-09-16 | `/query` free text is AND-of-all-lexemes under `english` stemming | — |
| 2026-09-16 | SKILL.md recall rationale updated for stemmed full-text recall | — |
| 2026-09-17 | Delivered read/write worker contracts, bounded deepsearch, createUuid payloads | #70 |
| 2026-09-17 | Authority resolution and divergence storage added | #70 |
| 2026-09-17 | Delegated read/write execution approved | #70 |
| 2026-09-17 | Both stdio MCP servers answer malformed JSON with a JSON-RPC parse error (-32700) and… | #70 |
| 2026-09-17 | Delivered memory-read/memory-write contracts, read-only client, bounded deepsearch, divergence composition/loop guard, createUuid payloads, cost evidence… | #70 |
| 2026-09-19 | Narrowed to "sole authority on the write path" | — |
| 2026-09-20 | "Sole interface" claim retired from living docs | #86 |
| 2026-09-20 | Replaced the outdated universal Codex custom-agent limitation with the current `.codex/agents/*.toml` capability and kept… | #86 |
| 2026-09-27 | Judge step | — |
| 2026-09-28 | Memory identity is group-scoped | — |
| 2026-09-29 | Redaction gate closed | #135 |
| 2026-09-29 | Fixtures encoded the overturned cross-group rule and CI certified it | #135 |
| 2026-09-29 | The semantic-dedup fixture set still encoded the overturned rule, and CI was certifying it | #135 |
| 2026-09-30 | Scorer printed accuracy as recall | — |
| 2026-10-01 | One foreground recall deadline | — |
| 2026-10-01 | Proxy and redirect guards tested where installed, not in isolation | — |
| 2026-10-01 | Unparseable base URL is `bad-base-url`, not a traceback printing userinfo | — |
| 2026-10-01 | Every persisting write is scrubbed, not just `set` | — |
| 2026-10-01 | Scrubber matches keys case-insensitively, as the Host binds them | — |
| 2026-10-01 | Redaction covers this repo's own secret shapes | — |
| 2026-10-01 | Generic secret rule no longer rewrites prose | — |
| 2026-10-01 | `uuid`/`version` path segments validated before any URL | — |
| 2026-10-01 | Every read surface frames recalled memory as untrusted data, from one notice | — |
| 2026-10-01 | Mid-response socket reset classified | — |
| 2026-10-01 | Hung store is `timed-out`, distinct from `unreachable` | — |
| 2026-10-01 | Digest names a human action | — |
| 2026-10-01 | An unparseable base URL is a classified `bad-base-url` refusal, not a traceback that prints… | — |
| 2026-10-01 | The redaction fingerprints cover the shapes this repository itself produces, quoted values no longer… | — |
| 2026-10-01 | A hung store raised a bare `TimeoutError` traceback instead of a classified error, and… | — |
| 2026-10-02 | MCP layer removed | — |
| 2026-10-02 | `resolve-group` has no dry-run, so a dry run resolves nothing | — |
| 2026-10-02 | Recorded the fresh-store preconditions and the read surface's token refusal | — |
| 2026-10-02 | The MCP read server now frames its output, classifies its blob fetch, and its… | #149 |
| 2026-10-03 | SKILL.md opt-out note names only kvasir `export`/`dump` | — |
| 2026-10-03 | Malformed `--payload` is a classified error | — |
| 2026-10-03 | Initialize defaults to Heimdallr when nothing is bound | — |
| 2026-10-03 | Two client hardening fixes | — |
| 2026-10-04 | `discrimination` on every record, not only scored ones | — |
| 2026-10-04 | An unexplained kvasir harness failure recorded, not dismissed | — |
| 2026-10-04 | Shipped threshold `0.85` | — |
| 2026-10-04 | "Saturates toward yes" was wrong | — |
| 2026-10-04 | Gate loads machine-credential settings, not only `os.environ` | — |
| 2026-10-04 | Best-attempt tie-break was dead code (`best_attempt(None, …)`) | — |
| 2026-10-04 | Ledger bounded | — |
| 2026-10-04 | A malformed rubric refuses instead of raising | — |
| 2026-10-04 | Redaction mapping fails closed in both directions | — |
| 2026-10-04 | Optional, off-by-default value gate added, client-side only | — |
| 2026-10-04 | The shipped default is now `0.85`, determinism is an assertion rather than a claim… | — |
| 2026-10-04 | The gate read only `os.environ`, so the settings `run.sh` publishes were invisible to it… | — |
| 2026-10-04 | "Best attempt" was never actually compared across rounds — the worktask's tie-break was dead… | — |
| 2026-10-04 | The attempt ledger grew without bound, and a discarded one reset the budget silently | — |
| 2026-10-04 | A malformed rubric raised a traceback instead of a refusal, and the loader now… | — |
| 2026-10-04 | The gate's redaction mapping failed open in the under-reporting direction, and is now refused… | — |
| 2026-10-05 | `pwd` no longer rewrites working directories | #182 |
| 2026-10-05 | Truncated keys, credential-word keys, `;params` base URLs and deepsearch's group selector closed | #182 |
| 2026-10-05 | Redactor timeout is `redactor-unavailable`, not a traceback the caller read as "skipped" | #182 |
| 2026-10-05 | A malformed probability no longer passes the gate | #179 |
| 2026-10-05 | Attempt ledger keyed by hash, not the raw subject (personal data on disk) | #179 |
| 2026-10-05 | `export`/`import` are real command aliases | #178 |
| 2026-10-05 | Store-direction switches named to match kvasir/ai-understanding | — |
| 2026-10-05 | Test names must match what the body proves | — |
| 2026-10-05 | Ledger cap tested on the write path | — |
| 2026-10-05 | Gate harness isolated from the operator's credentials file | — |
| 2026-10-05 | A held candidate is now queued, not printed and dropped | — |
| 2026-10-05 | The store-direction switches are named to match `kvasir`/`ai-understanding` | — |
| 2026-10-05 | `test_the_write_path_applies_the_cap` asserted a name its body did not earn, and the full review caught… | — |
| 2026-10-05 | The gate harness was not hermetic: every case silently read the operator's real `~/.mimisbrunnr/credentials`… | — |
| 2026-10-06 | Preflight entries are checked, not just the lists | — |
| 2026-10-06 | Credential loaders read presence, not truthiness, so an empty override is honoured | #190 |
| 2026-10-06 | Persisted values pass both redaction steps | #190 |
| 2026-10-06 | No personal data in scratch files, across every instruction that writes one | #188 |
| 2026-10-06 | Fixed by defect class | #186 |
| 2026-10-06 | Host spelling of the write token refused | #184 |
| 2026-10-06 | Every verdict needs a reason | #184 |
| 2026-10-07 | Only a missing ledger is a first run | — |
| 2026-10-07 | Calibration counts hold-side role clearing as bleed | — |
| 2026-10-07 | Deepsearch's malformed-row rule names `version` | #200 |
| 2026-10-07 | `query` refuses rows without `uuid`/`groupUuid`/`version` | — |
| 2026-10-14 | The shared scripts moved out of this skill's folder (HLD-010 B2 / LADR-01) | #222 |
| 2026-10-14 | Subject-less gate records are scored every call; their budget is not durable | #222 |
| 2026-10-14 | Credential subshell covers each write-route invocation | #221 |
| 2026-10-14 | A success-shaped zero-write `set` answer marks nothing | #221 |
| 2026-10-14 | Read/write subagents removed (LADR-008, supersedes LADR-005) | — |
| 2026-10-14 | Export receipt prefers server-confirmed uuids | — |
| 2026-10-14 | Session marker: import marks the export boundary (HLD-007 LADR-11) | — |
| 2026-10-14 | Checkpoint stops when the proposed initiative is not the resolved group's | — |
| 2026-10-15 | consumer review 5538013336: Docs only: recall stdout keeps its notice line before the JSON… | — |
| 2026-10-15 | Consumer review 5535929651: `SKILL.md` states the implemented session-id order (`--session` first) and the unreadable-marker… | — |
| 2026-10-15 | G1: held candidates persist as `Status = proposed` | — |
| 2026-10-15 | Consumer review 5539567320: the `set` receipt records the scrubbed subject, so a redacted description no longer reaches the marker file on disk in the clear | — |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
