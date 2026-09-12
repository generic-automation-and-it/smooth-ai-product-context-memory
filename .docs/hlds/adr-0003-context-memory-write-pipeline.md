# ADR-0003: Context-memory write pipeline and skill contract

**Status:** Accepted — specification
**Date:** 2026-09-10 · **Revised:** 2026-09-10 (contract-coherence pass — canonical five-stage numbering,
approval gating resolved to `status`-gating, digest reclassified as a post-write receipt, redaction column
withdrawn to match the digest-only decision, `set` stamp / dry-run / group-description operations added)
**Related:** [ADR-0001](./adr-0001-blob-storage-backend-and-addressing.md) (blob storage),
[ADR-0002](./adr-0002-persistence-layer-architecture.md) (persistence layer)

> **Provenance.** This ADR specifies the agent-facing skill contract and the write pipeline it executes.
> It is the **sole interface** to the context-memory store and the **sole authority** on the write path.
> It derives from the design-history record, which is **gitignored and local-only** and deliberately not
> cited as a reference. It was validated against three file-based simulation trials (also local-only —
> not cited). The persistence layer it writes against is ADR-0002, already merged and constraint-tested.

## Context

The persistence layer (ADR-0002) is merged and proven, but every judgement the design depends on is
unbuilt. Two constraints are **soft** by design — subject uniqueness and ticket-to-group uniqueness —
and three behaviours — semantic deduplication, link derivation, and redaction — cannot be expressed as
database constraints. Until the skill contract exists, the store is a schema nobody can safely write to.

Five decisions are entangled and must be specified together, because four of them are **stages of one
write pipeline** and the fifth is a property of that pipeline's pre-write round. Specifying them
separately produces the wrong shape. This is why this task exists as its own deliverable.

**The five decisions and the five pipeline stages are two different lists.** They are not a
one-to-one mapping, and conflating them is the easiest way to misread this document:

| Decision | Lands in pipeline stage |
|---|---|
| Redaction | 2 (Redact) |
| Deduplication | 1 (Preflight, candidate recall) → 3 (Dedupe, the decision) |
| Link derivation | 1 (Preflight, same traversal) → 3 (Dedupe / derive links) |
| Atomicity check | 4 (Atomicity check) |
| Ticket uniqueness | 1 (Preflight — same read, no extra traversal) |

The **pipeline stage numbers below are canonical**. Where a section heading carries a stage number, it
refers to that table.

Two constraints pin the design:

1. **Security is the dominant NFR.** Stored secrets are content-addressed and immutable (ADR-0001) — a
   leaked secret cannot be edited out afterwards, only orphaned. Mitigation must be preventive, not
   corrective.
2. **Memory poisoning.** Stored memories are re-injected into agent context later, so a wrong or
   malicious memory becomes a future instruction. The store is local, but local is not the same as
   trusted.

## Decision

### The five-stage write pipeline

All five stages are specified together because they are the write path. Ordering is fixed; the skill
runs them in this order and does not re-sequence.

| Order | Stage | What it does | Why at this position |
|---|---|---|---|
| 1 | **Preflight (clarification round)** | One **batched** cross-group read-before-write serving dedup, link derivation, and ticket-uniqueness. Array-in/array-out. Also detects **intra-batch** collisions (two candidates in one batch sharing a subject — neither written yet, so a per-record preflight misses it). | Must run before any write so the write decision (version vs new vs skip) is correct. All three need the same cross-group subject lookup, so one traversal serves all three — batched, not three separate passes. |
| 2 | **Redact** | Detect secrets/tokens/connection strings in the captured content and scrub them. | Must run **before** the blob write. Content addressing (ADR-0001) makes a blob immutable and its hash stable — a leaked secret cannot be edited out later, only orphaned. This ordering is non-negotiable. |
| 3 | **Dedupe / derive links** | The cross-group semantic subject match (drives version-bump vs new-memory vs skip) and typed-link derivation, both from the preflight. | Same cross-group lookup, applied to the write decision. Batched with dedup because both need the same read. |
| 4 | **Atomicity check** | Confirm each record is one atomic fact. Split bundled candidates; route the unprocessable remainder to `skipped`. | Must run before writing so no compound record reaches the store. |
| 5 | **Write** | One server-side transactional `set`. Version bump: flip old `is_current` off before inserting the new current, both in one transaction. | The only stage that persists. Owned by the API (WT-2), never sequenced by the skill. |

**Redaction is stage 2, before the blob write.** The immutability rationale is the reason for the
ordering: an object's address is the SHA-256 of its raw content, so identical bytes always yield the
same address and a stored object can never change. A leaked secret written to a blob therefore has a
stable hash and an immutable object; the only remedy is to orphan it and write a redacted copy, which
loses the original and pollutes the store with addresses that no longer resolve to wanted content.
Preventing the secret from reaching the blob at all is the only clean remedy.

### Redaction — pipeline stage 2

- **Detection strategy:** fingerprint-based — pattern match against well-known secret shapes
  (long-lived API keys, JWT-shaped tokens, `postgres://`/`mysql://` connection strings, cloud
  provider key formats, `password=`/`token=`/`secret=` assignments). The exact shape list is
  WT-3's implementation detail; the contract here is that a candidate fact with a detected sensitive
  span must be scrubbed before the blob write.
- **Failure mode:** **redact-and-flag**, not reject. A memory that leaks a secret still carries value;
  rejecting it loses the knowledge for a scrubbing problem. The sensitive span is replaced with a
  placeholder, and the digest records that redaction fired on that fact.
- **What is recorded:** digest-only. The fact that redaction fired and on which candidate is in the
  digest; the scrubbed content and the secret itself are **never logged** (PERSISTENCE_AGENTS logistic
  constraint — never log memory content). See LADR-003 in the skill's AGENTS.md.

### Deduplication — pipeline stage 3 (recall in stage 1)

- **Match on `description`** (the subject, stable side of the subject/claim split), **across groups**,
  not within.
- The database offers only an exact `subject_slug` unique backstop — a unique `(group_id, subject_slug)`
  index. Semantic equivalence — *"PostgreSQL is the storage engine"* vs *"we store in Postgres"* — is
  the skill's job, not the database's.
- **Candidate recall:** narrowed by facet/kind first, then the LLM judges a bounded top-N. `pg_trgm` +
  `similarity()` is **deferred** (see Alternatives) — the FTS configuration is `'simple'` (no stemming),
  so the recall surface is weaker than ideal, but index-first retrieval stays valid below ~1k records.
- **Outcome:** a candidate whose subject matches an existing memory becomes a **version bump** (same
  subject, new claim), not a duplicate insert. Two trials independently named semantic subject-matching
  the top write-path risk in the whole project.

### Link derivation — pipeline stage 3, batched with dedup (recall in stage 1)

- Nothing currently creates `MemoryLink`, so without this stage R10 would stay decorative forever.
  Three trials produced zero links without anyone noticing.
- **Placed in the pre-write clarification round, batched with deduplication** (D47 confirmed), because
  both need the same cross-group subject lookup — one traversal serves two purposes.
- Derived links are reported in the digest, never derived silently. Each link carries a mandatory
  `reason` (non-null in the schema, which structurally forces a justification per edge).
- **Veto is `--dryrun`, not the digest.** `set` writes links in the same transaction as the memories, so
  a plain-`set` digest is a receipt — the links already exist when the human reads it. The pre-write
  inspection point is `--dryrun`, which runs the identical pipeline and renders the identical digest
  without persisting. Splitting link creation into a second, separately-confirmed call was considered
  and rejected (see Alternatives): it costs a round-trip and leaves a window where a memory exists with
  its justified edges missing.

### Atomicity check — pipeline stage 4

- All three trials showed the model bundles several facts into one record under pressure and will not
  self-police.
- The check is explicit: one memory = one atomic fact. Bundles split; the unprocessable remainder goes
  to `skipped`.
- Restated three times in SKILL.md (Listen, pre-write round, pipeline table) — deliberate reinforcement
  against drift, not redundancy.
- The digest carries a **`skipped` count**.

### Ticket uniqueness — pipeline stage 1, inside the preflight

- A ticket belongs to at most one group. Soft by design (ADR-0002); enforced in the same read-before-write
  pass as dedup, so it costs no extra traversal.
- On collision, the skill surfaces the conflict in the digest; it does not silently create a second group
  for a ticket already owned elsewhere.

### Decisions beyond the pipeline

- **Trigger cadence:** batched at an explicit end-of-task checkpoint (`set`). Never per-fact mid-work.
  Manual trigger (D29), the "synthesize-on-request" model.
- **`get` parameters:** free-text question **and** explicit filters (label/ticket/repo/initiative/
  scope/kind). Both supported.
- **`set` parameters:** the caller passes the evidence and pointers (ticket/repo/initiative/scope,
  content, business-time source date). The skill **derives** kind, facets, tags, scope, subject,
  summary, and keywords. The caller does not hand-specify them.
- **Tier selection on read:** `get` returns the **cheap fields** as an array (`name`, `description`,
  `statement`, `content_summary`, `kind`, `facets`, `tags`, `status`, `confidence`, scope, validity),
  and the model drills into the blob on demand, proxied through the API. The consuming model judges
  relevance; it holds task context the store does not.
- **Digest format:** created / versioned / linked / diverged / skipped / labels-proposed, each with a
  count. Every write reports; nothing mutates silently. On a plain `set` the digest is a **post-write
  receipt** — it is rendered after the transaction commits, so it supports auditing and promotion of
  `proposed` records, not veto. **The pre-write inspection point is `--dryrun`**, which runs the identical
  pipeline and renders the identical digest while persisting nothing. `diverged` is always `0` until V2.
- **Approval gating semantics:** the gate governs `status`, not persistence. A gated kind without
  `--approve` is **written** as `proposed` and promoted to `approved` by a later version bump. Withholding
  the write was rejected (see Alternatives) — it discards the fact if the session ends before approval.
- **Switches with implication rules:** `--deepsearch` (V1, deferred), `--dryrun` (full pipeline, digest
  without writing), `--approve` (skip approval gate for gated kinds). Cheap by default, expensive opt-in.
  `--dryrun` and `--approve` are mutually exclusive.
- **Scope enforcement at retrieval:** programme knowledge is never citable as shipped product behaviour
  (ADR-0002 `program` scope dimension). This is a **retrieval** rule with no test coverage anywhere yet
  (see Testing). The write path sets the correct scope on the group so the retrieval rule has something
  to enforce; it does not reject the write.
- **Summary and keyword generation (R13):** the skill owns it. The model identifier and prompt version
  are stamped on each summary (D42) so summaries can be regenerated in bulk. On generation failure,
  store unsummarised and flag for backfill — do not silently reject the fact. **These are additive
  columns** and enlarge the `append_only_guard` trigger's fixed equality list — see Schema impact below.
- **Memory poisoning:** the store is local but not thereby trusted as settled canon. `get` renders
  results as **quoted data** with `sources`, `status`, and scope, never as imperative text. `proposed`
  records are excluded or flagged by default. Approval gating defaults ON for gated kinds.
- **`valid_from`:** derived from the source date when known, not always `now()` — otherwise bitemporality
  is decorative exactly as MemoryLink was.

### Schema impact (raised, not silently assumed)

The contract as specified requires **one additive column** on `memory_version`: the model/prompt stamp
(D42), so a summary batch can be regenerated in bulk. **No redaction column is owed** — the redaction
record is digest-only (see Alternatives), which is precisely what keeps this to a single column. No
schema change is required to the septet of entities — the model-shape guard test (seven entity types) is
unaffected, since this is a column, not a table.

**However:** `append_only_guard` (in the migration) hardcodes a fixed equality list of content columns
at the top of its `UPDATE` branch. Any new column not added to that list becomes **silently mutable**
through a legal version-bump UPDATE. Therefore WT-2 must, in the same migration that adds any column to
`memory_version`, extend the trigger's equality list to include it. This is raised here rather than
silently assumed, per the WT-1 scope boundary.

## API surface list (for WT-2)

Every operation the skill needs, with inputs and outputs. The `Application/Features/` and `Common/`
layers are currently empty — all of this is net-new. **All memory/group identity in the skill contract
is `uuid`, never the surrogate `bigint`.**

| Operation | Method + path | Inputs | Outputs |
|---|---|---|---|
| **Write preflight** | `POST /api/context/preflight` | Batch of candidate facts (subject, claim, content, kind, scope, ticket refs, source date). | **Judges nothing.** Per candidate: exact-match candidates (`uuid`, `group_uuid`, description, subject slug, kind, facets), ticket-uniqueness conflicts, intra-batch collision notices. **Writes nothing.** The semantic dedup decision (new / version-of-`uuid` / skip) and proposed links `[{target_uuid, relation, reason}]` are produced by the skill's `/query`-recall + LLM-judgement step, not by this endpoint. |
| **Set (write)** | `POST /api/context/memories` | The resolved write(s) from preflight: new memories or version bumps, each with derived subject/claim/kind/facets/tags/summary/keywords/sources/valid_from/valid_until/confidence/status, **the summary model identifier and prompt version (D42 stamp — the one additive column)**, and derived links. `status` is `proposed` for gated kinds unless the caller passed `--approve`. Also optional group resolve-or-create params. | The digest: `{created, versioned, linked, diverged, skipped, labels_proposed}` each with count, plus per-item `uuid` and `blob_address`. One transactional call; owns `is_current`. |
| **Set (dry run)** | `POST /api/context/memories?dryRun=true` | Identical body to `set`. | Identical digest shape, **nothing persisted, no `blob_address`, no `uuid` for planned creates** (identity is minted at persist time). This is the pre-write veto point; the endpoint must share one code path with the real write, or the dry run stops predicting it. **Implemented as one shared plan:** every verdict — subject collision, missing version target, unknown link endpoint, already-present link, already-present label — is reached before the persist step branches, so both paths return the same counts and fail on the same requests. |
| **Append group description** | `POST /api/context/groups/{uuid}/descriptions` | group `uuid` + description text. | New `GroupDescription` version. `GroupDescription` is append-only history with its own version chain (ADR-0002), so it cannot be updated in place and is not covered by group resolve-or-create, which only sets the first one. |
| **Resolve-or-create group** | `POST /api/context/groups/resolve` | ticket(s) / repo / initiative / scope. | Match existing group `uuid` by ticket, or create one (synthetic `local:<guid>` ticket when untracked). Repo, initiative and scope apply **on create only**. |
| **Update group** | `PATCH /api/context/groups/{uuid}` | Any of repo / repo_url / initiative name / scope dimension / scope identifier. Null leaves a field unchanged. | The updated group. Resolve only ever sets these at creation, so this is the only way to correct them. A dimension of `customer`/`program` without an identifier is rejected **against stored state**, not just the request body. |
| **Get (cheap fields)** | `POST /api/context/query` | Free-text query and/or filters: facets, tags, label, ticket, repo, initiative, scope, kind, status, `includeProposed`, `currentOnly` (default true), **`asOf` (business-time instant the claim must be valid at)** and **`limit` (default 50, max 200)**. | Array of cheap-field rows (no blob). Empty is a normal `200`. |
| **Get blob drill-down** | `GET /api/context/memories/{uuid}/versions/{version}/blob?scope={dimension}` | memory `uuid` + version, plus the scope the caller is reading as. | Blob content, **proxied through the API** so the store's own URLs never reach the caller *and* the scope rule applies: a dimension hidden from an open query is `403` here unless the caller names it. Holding a `uuid` is not authority to read programme knowledge as product fact. |
| **Get version history** | `GET /api/context/memories/{uuid}/versions` | memory `uuid`. | Version chain (cheap fields per version). |
| **Create link** | `POST /api/context/links` | `{source_uuid, target_uuid, relation, reason}`. | Confirmed link, or rejection (self-link `400`, duplicate `409`). Inside `set`, a duplicate is **skipped and counted**, not fatal — a stale derived link must not discard the capture it came with. |
| **Read facet vocabulary** | `GET /api/context/labels` | — | The derived `label_usage` view **unioned with** the advisory registry: every facet in use plus every registered label. `status` is the registry status, or null for a facet in use that was never registered — that drift is what the endpoint exists to reveal. |
| **Propose label** | `POST /api/context/labels` | `{name}`. | Draft label (registry is advisory — no FK; proposed, not enforcing). |
| **Read initiatives** | `GET /api/context/initiatives?status={status}` | Optional status filter. | Registry rows (name, description, status). Groups reference an initiative **by name**, so the caller needs to discover which names exist. |
| **Upsert initiative** | `POST /api/context/initiatives` | `{name, description?, status?}`. | The initiative and whether it was created. Idempotent by name because the name is the wire identity (the entity has no `uuid`); archiving is the same call with `status`. |

**Rules the API enforces (so the skill need not):** version-bump ordering and the single transaction;
`is_current` ownership; source ≠ target on links; `uuid`-based addressing; blob proxying *with* scope
enforcement; subject uniqueness within a group, reported before the write as well as at it; scope
filtering on every retrieval path. The skill owns judgement; the API owns mechanics.

**Retrieval is executed by the database, not by the handler.** Every predicate above — full text,
facets, tags, scope, kind, status, validity, current-only — is translated to SQL against the indexes
ADR-0002 defines. Materialising rows and filtering them in the handler would defeat those indexes and
would pull whole version chains across the wire on a current-only query, which is the default.

## Alternatives considered

- **Redaction at read time instead of write time (rejected).** By then the secret is already in the
  immutable blob and the DB summary. Same immutability problem, worse: the secret has been persisted.
- **Reject (not redact) on secret detection (rejected).** Loses the knowledge for a scrubbing problem.
  The capture is additive by design; the secret is incidental.
- **Redaction record persisted as `redactions jsonb` on the version vs digest-only (decision: digest-only
  for now).** Persisting gives an audit trail, but PERSISTENCE_AGENTS forbids logging content and adding
  a column means extending the trigger list. Chosen digest-only; revisitable if an audit trail is owed.
- **`keywords` as a new `text[]` column vs folded into `content_summary` (decision: fold into
  `content_summary` prose).** ADR-0002 promises "summary and keywords", but the schema has only
  `content_summary`. A separate `keywords text[]` + GIN is a clean additive migration, but routing them
  into `tags` would pollute the GIN containment filter and break "tags are classification, not derived".
  Chosen: keywords become part of the summary prose (zero migration, already indexed). Revisitable if
  search quality needs the separate column.
- **`pg_trgm` + `similarity()` for candidate recall (deferred).** The FTS config is `'simple'` (no
  stemming, no synonyms), so recall is weaker than ideal. But index-first retrieval outperforms vectors
  below ~1k records and `pg_trgm` is an additive migration — defer until measurement justifies it.
  Chosen for now: narrow-by-facet/kind then LLM-judge a bounded top-N.
- **Per-record preflight (rejected).** Misses intra-batch collisions — two candidates in the same batch
  sharing a subject, neither written yet. The preflight is batched array-in/array-out.
- **Link derivation as a separate background pass (rejected).** Defers value and needs its own trigger.
  D47 confirms the pre-write-round placement.
- **Link derivation at capture time (rejected).** Too early — the related memory may not exist yet.
- **Skill sequences the version bump (rejected).** The skill cannot touch `is_current`; the API owns it
  in one transaction. A failure between two round-trips strands zero current versions.
- **Blob `DeleteAsync` on a redaction orphan (rejected).** Identical bytes share one address; deleting
  can destroy content a different version still references. "Orphan" = drop the DB reference only.
- **Approval gating OFF by default (rejected).** The "ask about what is not reversible" rule makes
  gated kinds (`rule`/`nfr`/`decision`) default to `status: proposed`.
- **Gating by withholding the write vs gating the `status` (decision: gate the `status`).** Withholding
  the write until a human approves loses the fact outright if the session ends first, and the checkpoint
  is precisely the moment the session is ending. Gating `status` instead persists the fact, keeps it out
  of retrieval (`proposed` is excluded or flagged), and makes promotion to `approved` an ordinary version
  bump. Becoming *citable canon* is the irreversible step; being recorded is not.
- **Link creation as a second, separately-confirmed call (rejected).** It would give a true pre-write
  veto for links, but at the cost of a second round-trip and a window in which a memory exists without
  the edges that justify it. `--dryrun` already provides pre-write inspection over the whole batch, so the
  digest is deliberately a receipt rather than a gate.
- **`valid_from` defaulting to `now()` (rejected).** Makes bitemporality decorative; derive from the
  source date when known.

## Consequences

**Positive**

- The soft constraints (subject uniqueness, ticket uniqueness) are now enforced — by the skill, in the
  same pre-write pass.
- `MemoryLink` gains a real creator; R10 stops being decorative.
- Secrets are scrubbed before they reach an immutable blob.
- One fact = one memory; bundles are split, `skipped` is surfaced in the digest.
- Every write reports; nothing mutates silently.

**Negative / accepted costs**

- **Every write is an LLM call** (R13) for summary, keywords, dedup judgement and link derivation. The
  dominant cost; made explicit in the skill README.
- **Semantic dedup is the top risk.** A wrong match is expensive and silent (near-duplicate insert vs
  false version bump rewriting canon). Tested adversarially; restated three times in SKILL.md.
- **FTS is `'simple'`** — candidate recall is weaker than ideal until `pg_trgm` or stemming lands.
- **One additive column + a trigger-list change** are owed in WT-2 (the model/prompt stamp). The
  digest-only redaction record is what keeps it to one.
- **Memory poisoning is mitigated, not eliminated.** Quoted-data rendering and approval gating reduce,
  but do not remove, the risk of a stored memory reading as an instruction.
- **The skill is the single point of trust** for the soft constraints. A wrong judgement is uncorrected
  by any database constraint.

## Testing

No code is written by WT-1, so no test tier applies to it. The contract must instead specify **how WT-3
(the skill) will be tested**, and walk the contract against the three trial corpora.

### Walk against the trial corpora

The three trials (held in the local-only design-history record) were file-based simulations of the write
path. The contract now prevents these findings from recurring:

| Trial finding | What the contract now does about it |
|---|---|
| Subject-string matching is the top write-path risk | Dedup is explicit cross-group semantic matching (stage 3), restated three times in SKILL.md. The slug index is documented as exact-match backstop only. |
| Atomicity held only because a generator enforced it | Atomicity is an explicit check (stage 4) with a `skipped` count in the digest, restated three times. |
| Divergence never fired — corpus has no live conflicts | Recognized as a fixture limitation. Divergence is deferred to V2 with its own adversarial fixture (below), never claimed validated on design-doc capture. |
| Link derivation had no home; zero links across three trials | Link derivation is a named phase (stage 1 preflight + stage 3), batched with dedup, surfacing proposed links in the digest. |
| Bitemporality untested; `created_on` never emitted | `valid_from` derives from source date when known; the write contract carries both axes. |
| Facet clustering result was circular (closed list, not AI derivation) | The contract's dedup test drives the LLM semantic matcher, not a closed-list heuristic (below). |

### WT-3 semantic-dedup test approach

Semantic dedup is the top risk and needs a stated test approach, including an **adversarial fixture**,
because a coherent corpus contains no live contradictions.

- **The test drives the LLM semantic matcher**, not a string heuristic. The trial's `softMatch`
  (word-overlap ≥ 0.6 on words > 3 chars) is the documented failure — it "caught null" on
  *"Postgres is the storage engine"* vs *"we store in Postgres"* — and `run-trial.js` is the
  circular-encoding flaw (it encodes the rules then observes they hold). String/slug matching is
  asserted only as the **fast-path pre-filter** (negative-only guarantees), never the sole dedup decision.
- **Adversarial fixture**: enumerate both positive pairs (semantically equivalent, surface-different →
  must dedup) and negative control pairs (related but distinct → must NOT dedup), asserting a decision
  per pair, so the test measures **recall and precision**, not just "dedup held".
  - Positive example: `"PostgreSQL is the storage engine"` vs `"we store in Postgres"` → dedup (version).
  - Negative control: `"Auth issues JWTs with one-hour expiry"` vs `"Auth uses a revocable session
    cookie"` → NOT dedup (distinct claims).
- **Pass criterion is countable and non-circular**: e.g. N equivalent pairs across groups collapse to
  M unique subjects; the digest shows an exact `skipped` count; each pair yields its asserted decision.
- **Test home (WT-3 implemented):** the committed L0 harness `.agents/skills/context-memory/tests/run_tests.py`
  unit-tests the deterministic plumbing (redact.py no-leak + rule-name digest, atomicity bundle detection)
  and is CI-gatable. The on-demand LLM fixture set at `.agents/skills/context-memory/tests/fixtures/scenarios.json`
  (with `score_fixtures.py`) provides authored positive/negative scenarios — the adversarial dedup pairs,
  the negative controls, and the V2 divergence strip — scored here for recall AND precision, not CI-gated.

### Divergence fixture (V2)

Divergence needs a separate adversarial fixture, not a design-doc capture. The strip rule:

- Take the documented design reversals (e.g. supersession-by-archive → versioning,
  three-tiers → two → four fields, SQLite → PostgreSQL).
- Capture **both** the original and the reversed position as two separate candidate facts, with the
  reversal metadata stripped — remove `reversal:` annotations inside the evidence and the "current-only"
  directional framing — so both present as co-current unordered claims.
- Seed **distinct subjects** so divergence is isolated from dedup and version-bump (otherwise mechanism
  attribution is ambiguous).
- Assert the result is a divergence record surfacing both sides with no ordering (a `divergence` kind
  plus `contradicts` links), not a version bump or a dedup collapse.

## Open items

- **Deferred with owner:** `--deepsearch` (V1), divergence (V2 — needs no table, being a `divergence`
  kind plus `contradicts` links), semantic/vector search (deferred; index-first below ~1k records),
  `pg_trgm`/stemming for candidate recall (until measurement), `keywords` as a separate column (until
  search quality demands). Each is recorded so WT-3/WT-2 know it is not forgotten, not silently dropped.
- **WT-2 owns:** the additive migration for the model/prompt stamp, including the `append_only_guard`
  equality-list extension in that same migration. No redaction column is owed (digest-only).
- **V2 divergence fixture:** take documented design reversals from the design history, capture both the
  original and reversed position with reversal metadata stripped, and genuine unordered conflicts result.
  See the Testing section.
