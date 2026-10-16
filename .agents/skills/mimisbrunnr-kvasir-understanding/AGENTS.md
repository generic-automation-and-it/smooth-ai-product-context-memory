# AGENTS.md - mimisbrunnr-kvasir-understanding

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — the load, import, export and dump procedures, the marker and the gates live there.

## Non-Negotiables

- **Env vars and who receives them.** This skill reads no store token: store access runs through
  `mimisbrunnr-odin-context-memory`'s clients. `import` strips every write-token spelling from the read
  client's environment (names mirrored from the capture client's `WRITE_TOKEN_NAMES`, held equal by a test).
  `CONTEXT_MEMORY_DECISIONS_ENABLED`, `CONTEXT_MEMORY_DECISIONS_DEDUP_ENABLED` and
  `CONTEXT_MEMORY_REVIEW_QUEUE` select the shared gate, dedup and review-queue behaviour; the decision key
  stays in `decisions_gate.py`, which sends it only as its bearer header.
- **A byte-identical re-export is not deduped.** `SetMemories` versions any item sent with a `uuid` and
  compares no content, and preflight returns no statement to compare, so re-exporting an unchanged subject
  writes a new empty version. Content dedup needs a server-side skip or the current statement in
  preflight — not a client-side compare here.

## Architecture Decisions

These IDs are HLD-007's; `SKILL.md`, the README, the scripts and the tests cite them.

### LADR-02 — Load injects into context; the store-facing direction is a separate verb

- **Status:** Accepted — which verb is superseded by LADR-11 · **Date:** 2026-09-19
- **Decision:** `load` writes nothing to the store; moving material into the store is its own verb.

### LADR-03 — A store write funnels through the capture path

- **Status:** Accepted · **Date:** 2026-09-19
- **Decision:** `export` hands material to the capture path (now the `export` verb); never a direct write.

### LADR-07 — `--currentsession` dumps to a local folder

- **Status:** Accepted · **Date:** 2026-09-19
- **Decision:** the dump is an export, not a store write, and is redacted before it is written.

### LADR-09 — `load` reads `.understanding.md` units and store folders

- **Status:** Accepted · **Date:** 2026-09-19
- **Decision:** a unit file and a store folder are structured input, not foreign prose. `load` is the
  verb that reads them; `import` queries the live store and refuses a path (LADR-11).

### LADR-11 — Store-facing verbs match `ai-understanding`

- **Status:** Accepted · **Date:** 2026-10-14
- **Decision:** `export` is session → store through the capture path; `import` is store → session through the
  read client; `import <input> --store` is deprecated, not repurposed. An import marks the working point and
  a default export cuts at it.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-10-15 | Ralph loop: the queue record's session comes from the marker's resolver, not a third one; the write-only `_supersession` flag is gone | — |
| 2026-09-19 | `--all` with `--asof` counts both omission reasons separately | — |
| 2026-09-19 | Store-export import is understanding-only | — |
| 2026-09-19 | Hard-wrapped prose is not split per physical line | — |
| 2026-09-19 | Implemented `load`, `import --store`, `dump` | — |
| 2026-09-20 | Input is never dropped silently | #86 |
| 2026-09-21 | Portable skill metadata added | — |
| 2026-09-24 | Malformed redactor output refuses the dump instead of raising | #101 |
| 2026-09-24 | Reads the `ai-understanding` `.understanding.md` format | #101 |
| 2026-09-24 | Migrated the `smooth-devex-template` Understanding changes that apply to a loader | #101 |
| 2026-09-26 | README gained "How this relates to ai-understanding and to harness compaction" | — |
| 2026-09-30 | Cold-agent walk harness added | — |
| 2026-10-01 | A store load is a reported whole-record budget (`--max-chars`) | — |
| 2026-10-01 | Recorded walk gated on its scores, not its question count | — |
| 2026-10-01 | A decline is only the instructed phrase `not in context` | — |
| 2026-10-01 | Bare `unavailable`/`absent` no longer score confabulation as a decline | — |
| 2026-10-02 | `--dontask` accepted for forward compatibility | — |
| 2026-10-02 | Verbs aligned with `ai-understanding` | — |
| 2026-10-02 | `render_store` defaults to a bounded budget, never unbounded | — |
| 2026-10-02 | The two store-facing verbs are named to match `ai-understanding` (LADR-11) | — |
| 2026-10-02 | `render_store`'s `max_chars` now defaults to `DEFAULT_MAX_CHARS`, not `None` | — |
| 2026-10-03 | Over-cap batches auto-split into ≤20 chunks | — |
| 2026-10-03 | A stale `import --heimdallr` is refused with a pointer | — |
| 2026-10-03 | `import` takes only what the caller binds | — |
| 2026-10-03 | Heimdallr tests stub `initiative_exists` so they need no store | — |
| 2026-10-03 | npm package ships the Heimdallr scripts the client resolves by path | — |
| 2026-10-03 | A supplied field is final | — |
| 2026-10-03 | Heimdallr autofill (`--heimdallr`, default on) | — |
| 2026-10-03 | Renamed to `mimisbrunnr-kvasir-understanding` | — |
| 2026-10-03 | `export` auto-splits an over-cap batch and auto-versions a duplicate subject | — |
| 2026-10-03 | `HeimdallrAutofillTests.setUp` now stubs `initiative_exists` to a definite `(False, "absent")` | — |
| 2026-10-03 | Two distribution fixes | — |
| 2026-10-03 | Heimdallr precedence hardened: supplied fields are never touched | — |
| 2026-10-04 | Gate refusals said "Nothing was written" and then wrote | #174 |
| 2026-10-04 | `audience:*` tags carry their score margin | #174 |
| 2026-10-04 | Rubric v2 | #174 |
| 2026-10-04 | Survivors kept by index | #174 |
| 2026-10-04 | Optional value gate in `export`, off by default | #174 |
| 2026-10-04 | Contract text matches the chunking client | #174 |
| 2026-10-04 | `audience:*` tags now carry how much to trust them | #174 |
| 2026-10-04 | The value gate could silently drop records, and now keeps every candidate a score… | #174 |
| 2026-10-04 | Two pieces of the contract text still described the pre-chunking client, so the first… | #174 |
| 2026-10-05 | Ledger reset and eviction disclosures reach the operator | #182 |
| 2026-10-05 | Dry-run probe checks the gate's redactor | #182 |
| 2026-10-05 | A dry run probes instead of scoring, so it spends no attempt budget | #182 |
| 2026-10-05 | Export honours the gate's hold/mark setting and keeps audience tags and source scope | #179 |
| 2026-10-05 | `dump` redacts personal data (emails, UPNs), not only secret shapes | #179 |
| 2026-10-05 | `export --write` never defaults its input to whatever dump is newest | #178 |
| 2026-10-05 | `load`/`export` default their input to the current session | — |
| 2026-10-05 | Audience tags use the stripped role (`" engineer "` passed the blank check raw) | — |
| 2026-10-05 | Hung gate, non-boolean `passed`, non-list `passingRoles` are uninterpretable, not answers | — |
| 2026-10-05 | A valid-JSON gate report of the wrong shape skips the gate instead of crashing | — |
| 2026-10-05 | Gate tests isolate the machine credential file, which seeds settings at import | — |
| 2026-10-05 | Refusal tests assert a stopped export, not a note label | — |
| 2026-10-05 | `export` queues held candidates instead of printing and dropping them | — |
| 2026-10-05 | `dump --from` refuses a raw transcript-sized blob | — |
| 2026-10-05 | The defaulted input was unsafe on the write path and chose the wrong dump… | #178 |
| 2026-10-05 | A whitespace-padded role was tagged with its padding | — |
| 2026-10-05 | Three gate outputs this client could not interpret were read as answers: a hung… | — |
| 2026-10-05 | A gate report that was valid JSON of the wrong shape killed the export… | — |
| 2026-10-05 | My own two new cases were not hermetic, and the gate caught them exactly… | — |
| 2026-10-06 | Scoped records beside unscoped ones are refused, not silently re-scoped | — |
| 2026-10-06 | Confidence `0` is flagged, not read as "no confidence" (`or ""` dropped it) | #190 |
| 2026-10-06 | `dump` passes binding values and `--session-name` through both redaction steps before writing | #190 |
| 2026-10-06 | Only a list of objects with a text `statement` is a store export | #188 |
| 2026-10-06 | Redactor and atomicity answers paired by `candidate_index` | #186 |
| 2026-10-06 | The read subprocess strips every write-token spelling the read client refuses | #184 |
| 2026-10-06 | Walk scorer rejects a negated key fact (substring `must_contain` passed "not postgres") | #184 |
| 2026-10-07 | No explicit selector can still bind via Heimdallr autofill | — |
| 2026-10-07 | Walk Q4's key fact is the relationship (`redis holds the cache`), not the word… | — |
| 2026-10-07 | `SKILL.md` capture order matches the client | #200 |
| 2026-10-07 | Absent-initiative check runs before the decision gate, so that refused write spends no attempt | — |
| 2026-10-07 | `links: []` pinned on the payloads actually sent, not a source-text count a quote… | #196 |
| 2026-10-13 | A defaulted input now writes only when it provably is this session's dump | — |
| 2026-10-13 | Export date-only source validity as midnight UTC for the Instant API | — |
| 2026-10-14 | The shared-code move reached this client (HLD-010 B2 / LADR-01) | #222 |
| 2026-10-14 | Input-resolution fixtures stub `dump_root`: `matching_session_input` runs before the stubbed newest-dump lookup against the real… | #224 |
| 2026-10-14 | Gate positional-index purge reaches the operator | #222 |
| 2026-10-14 | Export receipts record `originVersion`: where the candidate carried one, so the digest can say… | — |
| 2026-10-14 | `export` writes by default; `--dryrun` previews (replaces the `--write` opt-in) | #222 |
| 2026-10-14 | Session marker: imports and exports mark the boundary (LADR-11 + export-marks amendment) | — |
| 2026-10-14 | `--write` refuses when the bound initiative is not the resolved group's | — |
| 2026-10-15 | consumer review 5538013336: Held candidates that differ only by case or punctuation no longer… | — |
| 2026-10-15 | Review-loop simplifications | — |
| 2026-10-15 | Consumer review 5539567320: LADR-09 names `load` as the file-reading verb (`import` queries the live store); marker tests resolve paths with `marker_path` | — |
| 2026-10-15 | Consumer review 5535929651: `load`/`import` mark only the records they rendered (`render_store_records` / `render_table_records`), not… | — |
| 2026-10-15 | G1: `export` persists held candidates as `Status = proposed` | — |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
