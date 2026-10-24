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
- **A user-facing message long enough to be prose lives in `assets/`, not in the code.** One message
  per `.md` file, loaded by name through `read_asset`/`render_asset` in
  `scripts/understanding_client.py`, filled by single-pass substitution on `{{placeholder}}` — a value
  is inserted verbatim and never re-scanned, so a value that itself contains `{{…}}` prints
  literally, the same bytes the replaced f-strings produced. The file body is
  the printed text **verbatim** — one trailing newline is the file's own and is stripped — so moving a
  message must not change a byte of stdout or stderr. A missing, unreadable, empty or half-filled file
  is `AssetUnavailable`, which `main` turns into `REFUSED: … Nothing was written.` and exit 1: never a
  silent inline fallback, and never the body in the error. A one-line aside stays inline (below the
  three-printed-line / two-call-site threshold); the skills root `AGENTS.md` holds the wider rule.
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

## Assets

Strings the client loads instead of carrying inline. Every one is pinned byte-for-byte by
`SkillAssetTests` in `tests/run_tests.py`, which also holds the loader's refusals, the no-`.txt` rule
and both directions of the file/loader inventory.

| Family | Files | Loaded by |
| :---- | :---- | :---- |
| Deprecated spellings refused with their replacement (3–4 printed lines) | `import-deprecated-store.md`, `import-deprecated-heimdallr.md` | `cmd_import` |
| Gate refusals reused at two call sites | `gate-redactor-refusal.md` (`probe_decisions`, `gate_decisions`), `gate-misconfigured-refusal.md` (same pair, `{{detail}}` unifies the two variable names) | `probe_decisions`, `gate_decisions` |
| Export preview frame (4 printed lines) | `export-preview-dry-run.md` | `cmd_export` |
| Write-path frames (3 printed lines each; `{{output}}` is server-echoed payload derived from already-redacted candidates, printed to stdout exactly as the f-strings did — the asset file itself carries no record content) | `persist-proposed-frame.md`, `veto-dryrun-frame.md`, `wrote-frame.md` | `_persist_proposed_batch`, `cmd_export` |
| Marker note reused at three call sites | `session-marker-note.md` | `_mark_export_receipt`, `_mark_import` |
| Dump starting content (10 printed lines; R4's template case) | `session.template.md` | `session_template()` at `cmd_dump` — a function, not a constant: a module-level read would turn a missing asset into an `ImportError` before `main` could refuse. The loader strips a file's trailing newline and the function restores it, because `dump` writes the template as a file |

Deliberately **not** here — each is one printed line at one call site, so moving it would trade a
readable literal for a file read without meeting the bar: the store answers (`UNREACHABLE` /
`TIMED OUT` / `NO RECORDS MATCHED`), the `STORE READ FAILED` / `REFUSED: import …` / `NOT FOUND` /
`NO LOADABLE MATERIAL` / `NOT A STORE EXPORT` family, the three scope reconciliations, the dry-run
`matched` / multi-batch / understanding-kind lines, the three receipt lines, the product-link,
dedup, review-queue and redactor/atomicity/initiative refusals, and the rest of the decisions
family (gate-missing, gate-timeout, gate-failed, unreadable/unrecognised reports, `belowThreshold`
and records-list notes, ledger disclosures, `decisions: skipped …` notes). The dump's header block is
also left inline: it is an assembly of the two `GENERATED_FENCE` constants plus four interpolations,
and `strip_dump_boilerplate` depends on those fence constants — moving it would duplicate
byte-critical structure to externalise two lines of prose. Also not here:
`DATA_NOTICE`, the recall framing reused at three render sites — it passes the reuse bar but is
**not skill-specific prose**: it is the WET twin of the shared capture client's
`BANNER_PREFIX + RECALL_NOTICE` (`_mimisbrunnr-shared/scripts/context_memory_client.py:871-874`),
held byte-equal by odin's `test_the_shared_notice_is_defined_once`. Moving this half into an asset
would relocate one side of a pinned pair and add a file read to satisfy a test; the shared half
cannot follow (R1: no `_mimisbrunnr-shared/assets/`, and a shared script never resolves a caller's
assets), so both stay inline as pinned constants.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-10-24 | **Re-split against the strict bar: 16 → 9 → 10 assets.** Review found twelve of the moved messages fail the plan's own threshold — one printed line at one call site each (the store answers, the three scope reconciliations, the dry-run `matched`/multi-batch/kind lines, the three receipt lines) — and five qualifiers missed: the misconfigured-gate refusal (one line × two sites, `{{detail}}` unifies the two variable names), the session-marker note (× three sites), and the three 3-line write-path frames (`PROPOSED`, `set --dryrun`, `WROTE`). The twelve moved back inline byte-identical; the five moved in byte-identical. Measure is **printed lines** — source-line wrapping would qualify half the file (e.g. the 3-source-line "writing needs an explicit input" refusal stays inline) and defeat the YAGNI guard. A later pass added the eleventh qualifier R4 names by category: the 10-line dump template, now `assets/session.template.md` behind `session_template()` — a function, not a constant, so a missing asset is a named refusal from `main` rather than an `ImportError` at import, with the newline the loader strips restored because `dump` writes it as a file (314 cases). R3 note: the frame `{{output}}` values are server-echoed payloads derived from already-redacted candidates, stdout-bound exactly as the f-strings printed — the asset files carry no record content. The rest of the decisions family stays inline: each note is one line at one site, and moving all fifteen would be the clutter this bar exists to prevent. | this PR |
| 2026-10-24 | **The client's user-facing prose moved into `assets/`, loaded by name.** Sixteen messages — the store-answer refusals, both deprecated spellings, the gate's redactor refusal, the three scope reconciliations, and the `--dryrun` preview and write receipt blocks — now live one per `.md` file and are loaded by `read_asset`/`render_asset`. Not a behaviour change: each of the sixteen was compared byte-for-byte against the literal it replaced, read back out of the pre-move source — including the gate refusal, which was a module constant rather than a `print` argument — and every one is byte-pinned in `SkillAssetTests`, which also holds the fail-closed loader (`asset-missing`, `asset-unreadable`, `asset-empty`, `asset-placeholder-unfilled`), the no-`.txt` rule and both directions of the asset/loader inventory. A broken install is a named refusal from `main`, never a half-filled message. 303 → 312 cases. **Ralph loop RL-001:** chained `.replace` could not tell a substituted value holding `{{…}}` apart from a real placeholder — a record scoped literally `{{scope}}` re-exported under `--scope x:y` printed the false claim `scoped x:y`, and a value holding an unknown `{{…}}` refused blaming the install. Substitution is now single-pass (`re.sub` with a fill function): a value is inserted verbatim and never re-scanned, restoring the f-strings' exact bytes on that edge too; pinned by `test_a_value_containing_a_placeholder_is_inserted_verbatim`. 312 → 313 cases. **WET scope recorded:** no asset is needed by two skills, so nothing is duplicated (T3 N/A); the one shared prose — the recall notice — is the WET twin of the shared client's `RECALL_NOTICE` and stays inline as a pinned constant (`test_the_shared_notice_is_defined_once` in odin's suite), named in the Assets section so the threshold does not re-open it. | this PR |
| 2026-10-18 | **Session-export workflow leads `SKILL.md`; entry points no longer contradict the client.** A short eight-step workflow (complete client → eligibility → authorisation → stdin dump → recall → optional preview → write → receipt) and a *Preview limits* paragraph sit before the reference sections: a preview appends held candidates to the local review queue, resolves no group, probes rather than scores the value gate, and exits 1 before group/product/preflight when every candidate is held; atomicity signals are evidence, not a split plan. Drift repaired: the skills index advertised the deprecated `--store`; the README claimed every switch defaults off (Heimdallr autofill is on for `export`/`dump`) and described product edges as initiative-or-repo; `export --help` did the same and said `--dryrun` runs "every gate"; the `import --store`/`--heimdallr` deprecations labelled a writing `export <input>` as "to review" — it now carries `--dryrun`. `SessionExportGuidanceTests` pins each of these and round-trips the workflow's own stdin example (dump → export `--dryrun`, 2 candidates, no `set`/`resolve-group`). No verb, flag, exit code or refusal order changed. | — |
| 2026-10-17 | `export --product-name` now declares BOTH `(Product)-[:HAS_INITIATIVE]->(Initiative)` AND `(Product)-[:HAS_REPO]->(Repo)` when both anchors are bound; either alone attributes the memory (LADR-10), so a repo conflict (`409`) is reported as a NOTE and does not fail an export whose primary anchor already landed. The "initiative wins" heuristic is gone — `_product_target` returns a list and `_declare_product_link` loops | — |
| 2026-10-16 | **The export prints the paths the gate reports, not paths it recomputes** (`Ralph` RL-001). It derived the snapshot name a second time *after* scoring returned, so the two clock reads straddled the gate and the operator was told a file that did not exist — confirmed `...184017...` printed against `...184012...` on disk, and with a real batch the gap is a minute, so it was wrong essentially every run. The gate's report is the authority on what it used. Also: the default ledger directory is spelled once (`DECISIONS_CONTEXT`), `ledger_paths` is `budget_ledger_path` because it returns one path (RL-005, RL-006), and a dead `if snapshot_file is not None` guard is gone (RL-007). 295 -> 296 cases. | Ralph loop RL-001, RL-005..007 |
| 2026-10-16 | **The gate's two ledgers are named for the project and initiative.** `.context/<project>-<initiative>-decisions-ledger.json` holds the attempt budget and is **stable** — it must outlive a run, or the three-attempt bound becomes dead code and a record an agent rewrote three times is scored a fourth. A stamped sibling `...-<yyyymmdd-HHMMSS>-decisions-ledger.json` holds that run's verdicts. The project is the product name, the repository the fallback (always bound by write time), and `unbound` the last resort so a name is never empty; two initiatives therefore never share a budget. A product name is registry-supplied free text and is filtered to a safe alphabet, so a name like `../../elsewhere` cannot steer the write out of `.context/`. **`MIMIS_DECISIONS_STATE` still outranks the derived path**, and the snapshot is derived from the resolved budget, so pointing that at a temp file keeps a whole run — ledger *and* snapshot — out of the working tree.| **Every candidate now reaches the gate with an identity the ledger can key on**, which is what made the two files appear at all. The gate budgets a record to three attempts and stores that count on disk, and a record with no `subject`/`description`/`name` is keyed by **position** instead — scored as a first attempt on every call, with nothing ever written. The foreign-prose splitter emits `"description": None` by construction, so **every foreign export scored on position, for ever**: the gate ran, reported a healthy verdict count, and the bound it appeared to enforce was never applied to anything. The only trace was `durable: false` inside a JSON payload the export discarded. The subject is the existing `_subject()` — the one the writer already uses for `name`/`description` —
added to the gate's **copy**, not to the candidate, so capture and dedup keep keying on the store's
own subject. Writing a second derivation here would have keyed the ledger on something the store
never sees, and a rewritten candidate would lose its budget to a rename while its subject stood. The note now names any record whose budget did not persist, so this can never again be silent. Verified end to end: attempts 1, 2, 3 then `attempts-exhausted`. 285 -> 295 cases. | PR #278 |
| 2026-10-16 | `DEFAULT_MAX_CHARS` 12000 → 120000; the same number truncates a `load --format foreign` input | HLD-007 LADR-10 |
| 2026-10-16 | `export --reference-tickets <list>` sends the tickets as `ticketReferences` (ABOUT) instead of binding them for group resolution; the group binds `--tickets` when given, else a deterministic synthetic owner ticket `local:wet-<initiative-slug>-<provider>-<key>` so a re-export reuses one group. One `relates_to` twin link per candidate whose exact preflight match sits in the referenced ticket's owner group — mechanical (explicit flag + exact subject), never a semantic judgement. `initiative_mismatch_refusal` gained a fourth option; `import --table` renders a `Match` column when the query was ticket-filtered. | HLD-003 LADR-09 |
| 2026-10-16 | `import --product-name` recalls **through** the product-name registry — the read-side twin of the `export` flag that declares the product's graph edge: same spelling, opposite direction. A query field, so the server resolves the product to its initiatives and repos; an unregistered product is the same `404` naming `upsert-product-name`, never a silent empty. It is not the `product` scope dimension. Added because the Console generates per-memory import commands and had no way to express a product-level recall | — |
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
| 2026-10-16 | `export --product-name` declares `(Product)-[:HAS_INITIATIVE]->(Initiative)`, or `(Product)-[:HAS_REPO]->(Repo)` when no initiative is bound, after the memories are written; the register is read for a preview, so a dry run still needs no write credential. Initiative wins when both are bound (the repo edge is the one that can 409). Refused up front when the export binds neither anchor, and an unregistered product is named with its `upsert-product-name` fix. `--productname` accepted as an alias | — |
