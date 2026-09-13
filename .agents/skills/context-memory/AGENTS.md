# context-memory — AGENTS.md

## TL;DR

The sole interface to the context-memory store and the sole authority on the write path. Runs a fixed
five-stage pipeline (**preflight → redact → dedupe/derive-links → atomicity → write**), the last stage
being a single server-side transactional `set`; the skill performs the semantic deduplication, link
derivation, redaction and atomicity checks the database cannot express as constraints.

## Non-Negotiables

- **Never write mid-work.** Accumulate candidates silently during work; write only at the explicit
  end-of-task checkpoint (`set`). This is the manual-trigger design (D29), not a per-fact flush.
- **Don't "optimize" the SKILL.md by deduplicating the restatements.** The atomicity discipline and the
  cross-group subject-matching rule are each restated at three points in SKILL.md (Listen, the pre-write
  round, and the pipeline table). This is **deliberate reinforcement, not redundancy**. Three trials all
  demonstrated the model bundling compound records under pressure and stopping self-policing; two trials
  independently named semantic subject-matching the top write-path risk. Repeating these two rules at
  each phase is the countermeasure to that drift. Removing any one restatement reduces the reinforcement.
- **Never invent the write sequence.** The version-bump ordering (flip old `is_current` off *before*
  inserting the new current, both in one transaction) and the `SET LOCAL` bypass idiom for cascade
  deletes are pinned by `PERSISTENCE_AGENTS.md`. The skill follows them exactly; it does not re-derive
  them. See LADR-001.
- **Never expose or touch `is_current`.** `set` is a single server-side endpoint that owns the current
  flag. If the skill can mutate `is_current` directly it will sequence the bump and, on a failure
  between the two round-trips, leave a memory with **zero** current versions — a state no constraint
  forbids and nothing detects.
- **Redaction precedes the blob write.** Content addressing (HLD 001 (storage)) makes a blob immutable and its
  hash stable. A leaked secret cannot be edited out afterwards, only orphaned. This ordering is
  non-negotiable.
- **Deduplication is cross-group**, not within a group. Grouping is episodic (by ticket); the same
  subject legitimately arises under different tickets. A per-group check misses it. The database's
  unique `(group_id, subject_slug)` is an exact-match backstop only.
- **The registry is advisory.** `memory.facets` has no FK to `label` by design (HLD 001 (storage)). The skill
  proposes new labels; it must not treat the registry as a closed set.
- **Blobs are never deleted on the write path.** Content addressing means identical bytes share one
  address, and `IBlobStorage` has no refcount or enumeration. `DeleteAsync` on a redaction orphan can
  destroy content a different version still references. "Orphan" means *drop the DB reference only*;
  leaked-secret purge is out-of-band, not skill work.
- **Programme knowledge is never citable as shipped product behaviour.** Scope is enforced at
  retrieval, but the write path must set the correct scope on the group so the retrieval rule has
  something to enforce.

## System Context

The skill is a thin client over the HTTP API (PR #14), which is the only thing that touches PostgreSQL and
blob storage. The skill owns the judgement; the API owns the mechanics. Both soft constraints (subject
uniqueness, ticket-to-group uniqueness) and the write-time AI work (summary, keywords, semantic dedup,
link derivation, redaction) live here — none can be expressed as a database constraint.

```mermaid
flowchart LR
    A[Agent / Human] -->|capture candidates| B[context-memory skill]
    B -->|"set (preflight + one transaction)"| C[HTTP API]
    C --> D[(PostgreSQL)]
    C --> E[(MinIO blob)]
    B -->|"get (cheap fields, drill-down)"| C
```

## Architecture Decisions

- **LADR-001** (2026-09, accepted): Version-bump ordering is pinned by the persistence layer, not the
  skill. *Context:* `memory_version` is append-only with a partial unique index over `is_current` and a
  `BEFORE UPDATE OR DELETE` trigger permitting exactly one legal UPDATE (the `is_current` pointer
  transfer). *Decision:* the skill never sequences the bump; it calls one transactional `set` that flips
  the old current off and inserts the new current atomically. *Consequence:* removing the transaction
  or letting the skill drive `is_current` can strand a memory with zero current versions — a state no
  constraint forbids.

- **LADR-002** (2026-09, accepted): Link derivation is batched with deduplication in the pre-write
  round (D47). *Context:* three trials produced zero typed links, so `MemoryLink` would stay empty
  forever and R10 decorative. *Decision:* both link derivation and cross-group dedup need the same
  cross-group subject lookup, so one traversal serves both, and it must also detect **intra-batch**
  collisions (two candidates in the same batch sharing a subject — neither is written yet, so a
  per-record preflight misses it). Derived links are reported in the digest, never derived silently, and
  are inspectable before anything lands via `--dryrun`. *Consequence:* the preflight must be
  array-in/array-out, not per-record. **Note the limit:** `set` writes the links in the same transaction
  as the memories, so a plain-`set` digest is a receipt, not a veto gate — `--dryrun` is the only pre-write
  veto point. Do not reword this as "surfaced for veto"; that implies an approval round that does not exist.

- **LADR-003** (2026-09, accepted): Redaction failure mode is redact-and-flag, not reject. *Context:* a
  captured memory that leaks a secret still carries value; rejecting it loses the knowledge. *Decision:*
  detect (fingerprint-based) and scrub the sensitive span, record what was scrubbed in the digest. The
  record is digest-only — content is never logged (PERSISTENCE_AGENTS quality constraint). *Consequence:*
  a leaked secret is scrubbed before write, so the stored blob and DB never contain it; the digest is
  the only trace.

- **LADR-004** (2026-09, accepted): The skill-facing contract exposes `uuid`, never the surrogate
  `bigint`, for memory/group identity. *Context:* the surrogate is an internal FK join key; `uuid` is
  the logical identity that is stable across versions and groups. *Decision:* all skill inputs/outputs
  use `uuid`. *Consequence:* the skill cannot accidentally key on a row that a version bump re-points.

## Key Behaviors

- **Atomicity checked three times.** Restated at accumulation (Listen), at the pre-write round, and as
  the `skipped` count in the digest. The check is: one memory = one atomic fact. Bundles split; the
  unprocessable remainder goes to `skipped`.
- **Semantic subject matching is the skill's job.** The `subject_slug` unique index catches exact
  re-capture only. Word-overlap heuristics misfire on short subjects (proven in trial 3). Candidate
  recall is narrowed by facet/kind, then the LLM judges a bounded top-N; the match drives version-bump
  vs new-memory vs skip.
- **`valid_from` derives from the source date when known**, not always `now()` — otherwise bitemporality
  is decorative exactly as MemoryLink was. Business time and system time are never conflation (HLD 001 (storage)).
- **`get` renders results as quoted data** with `sources`, `status`, and scope. A stored memory is not
  an instruction; the store is local, not thereby trusted as settled canon. `proposed` records are
  excluded or flagged by default.
- **Approval gating governs `status`, not persistence.** `kind ∈ {rule, nfr, decision}` are **written**
  with `status: proposed` unless `--approve` is passed; they are not withheld from the store. Retrieval
  excludes or flags `proposed`, and promotion to `approved` is a later version bump. This is the "ask
  about what is not reversible" rule applied to *canon*: becoming citable is the irreversible step, not
  being recorded. A contract that instead withholds the write loses the fact if the session ends before
  approval — that reading is wrong wherever it appears.
- **Summary/keyword generation** is a write-time LLM call (R13). The caller does not hand-specify
  kind/facets/tags/scope/summary/keywords — the skill derives them. On generation failure, store
  unsummarised and flag for backfill; do not silently reject the fact.
- **Group resolution:** a ticket belongs to at most one group. Untracked work gets a synthetic
  `local:<guid>` ticket; no empty-ticket group is ever created.

## Test References

- **Committed L0 harness (CI-gatable):** `.agents/skills/context-memory/tests/run_tests.py` — stdlib
  `unittest` (no external runner). Unit-tests the deterministic plumbing: `redact.py` (planted
  credential never leaks; digest reports the rule name) and `atomicity.py` (bundle → split/skip).
  Run: `python3 .agents/skills/context-memory/tests/run_tests.py`.
- **On-demand LLM-eval fixtures (not CI-gated):**
  `.agents/skills/context-memory/tests/fixtures/scenarios.json` plus `score_fixtures.py`. Authored
  positive/negative scenarios for the semantic-dedup, atomicity, link and divergence stages, scored
  for recall AND precision against a countable expected-verdict set.
- The skill's test approach is specified in `docs/hlds/002-context-memory-write-pipeline/nfrs/NFR-02-deduplication-accuracy.md`. These
  are not this repo's L0/L1/L2 tiers, which apply to the C# API (PR #14).

## Requirements

Approved (2026-09-12) implementation plan for making this contract executable
against the HTTP API (PR #14). No C# changes. The plan detail and decision set live in the gitignored working
spec `.context/work-tasks/wt-3-phase3-spec.md`. Key decisions: semantic dedup composed from
`/query` recall (facets+kind, no free-text, `includeProposed:true`, `limit:200`) + LLM judgement,
with `/preflight` as exact-match backstop + intra-batch + ticket-uniqueness only (amends HLD 002 (write pipeline)'s
"Write preflight" API row); digest renders `skipped(atomicity)` separately from `skipped(duplicate-link)`;
redaction detector is a stdin→stdout fingerprint script reporting rule names only; 20-candidate cap
as a static configurable setting; divergence fixture asserts non-collapse (V1 `diverged:0` preserved).

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-10 | Created — contract for the sole interface to the context-memory store; fixed write pipeline; cross-group dedup, atomicity and secret-redaction ownership. | HLD 002 (write pipeline) |
| 2026-09-10 | Contract-coherence pass. Pipeline numbering pinned to five stages with **preflight as stage 1** (SKILL.md previously specified four, starting at redact). Approval gating resolved to *write-as-`proposed`* — SKILL.md previously said "do not write", which contradicted this file and the retrieval rule that excludes `proposed` records. Digest reclassified as a post-write receipt with `--dryrun` named as the only pre-write veto point (LADR-002 previously implied a veto round that `set`'s single transaction cannot provide). Intra-batch collision, source-date `valid_from`, and the summary model/prompt stamp added to SKILL.md, which the executing agent reads. | HLD 002 contract review |
| 2026-09-12 | `## Requirements` added — approved implementation plan (skill executable against the HTTP API; no C#). Key decisions recorded (semantic dedup via `/query` recall + LLM judgement; digest `skipped` segregation; redaction rule-name digest; 20-cap static setting; divergence non-collapse). | PR #17 |
| 2026-09-12 | **Skill implemented (no C#).** Added `scripts/context_memory_client.py` (14 subcommands, base-URL + health probe, `MAX_CANDIDATES`=20 cap, `--dryrun`), `scripts/redact.py` (stdin→stdout, rule-name digest, true-positive rules), `scripts/atomicity.py` (conservative bundle detector). SKILL.md gained `## Deterministic Components` (script mapping + two-call semantic-dedup composition) and `## Scope Prohibitions` (programme exclusion, no auto-403-`?scope=program` retry); digest `skipped` segregation added. HLD 002 (write pipeline) "Write preflight" API row amended to **judges nothing**. Test home: committed L0 harness `tests/run_tests.py` (12 tests) + on-demand fixtures `tests/fixtures/`. **Deferred:** the L0 harness is committed and CI-gatable but not wired into `pr-gate.yml` (approved plan's file set omitted a CI change). | PR #17 |
