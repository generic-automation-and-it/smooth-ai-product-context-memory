# mimisbrunnr-context-memory — AGENTS.md

## TL;DR

The sole authority on the write path. Runs a fixed
five-stage pipeline (**preflight → redact → dedupe/derive-links → atomicity → write**), the last stage
being a single server-side transactional `set`; the skill performs the semantic deduplication, link
derivation, redaction and atomicity checks the database cannot express as constraints. The read/load
sibling (`mimisbrunnr-understanding`) injects material into agent context and — only under `--store` —
hands imported material to this skill's capture path; it is a reader + conditional importer, never a
second writer.

## Non-Negotiables

- **Main thread never consumes raw store rows.** Retrieval and pre-write recall run in delegated
  read/write contexts. Read execution receives only the read API credential and read-only client;
  write execution receives discrete facts, never a transcript. API credentials enforce capability;
  prompt text alone is not a security boundary.
- **Project agent registrations live in `.agents/agents/` for Claude-compatible runtimes and
  `.github/agents/` for Copilot.** Skill-local files hold detailed contracts; project registrations
  make workers discoverable and point at them. Current Codex supports project custom agents through
  [`.codex/agents/*.toml`](https://learn.chatgpt.com/docs/agent-configuration/subagents), but this
  repository ships only Markdown Codex worker profiles, not TOML profiles, and their intended MCP/tool
  grants were absent from the contribution runtime. Treat a registration file or worker name as
  insufficient: delegation is supported only when the runtime exposes the capability-limited workers
  with their intended grants.
  Read worker has no generic
  shell/write-client access; its MCP server strips `CONTEXT_MEMORY_WRITE_TOKEN` and exposes only reads.
  Write worker also has no shell/file tools; typed write MCP methods bound its mutation surface.

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
- **The redaction gate is a gate on *recognition*, not on secrets.** `set` scrubs automatically and
  refuses to write when the scrubber cannot run, so it can never fail open — but `redact.py` matches
  fixed-shape fingerprints (`AKIA…`, `gh[pousr]_…`, PEM blocks, `secret=`/`token:`/`password=`
  assignments). A bare high-entropy value, an unlabelled base64 blob, an unusual vendor token format or
  a password in prose passes through untouched. Describe this control as *"recognisable secrets are
  gated, and the gate never fails open"*; **do not** describe it as "sensitive material never reaches
  storage", which overstates it. Widening the rule set is the lever, and that is a deliberate separate
  decision. `redact.py` carries the same statement at its top.
- **Subject matching reads across groups, but a version bump is scoped to the writing group.** A memory's
  identity is `(group, uuid)`, decided 2026-09-28 with HLD-002 amended to match. The recall is
  deliberately cross-group so a same-subject memory elsewhere is *seen*; only a match inside the
  request's `groupUuid` may be bumped, and a cross-group match becomes a new memory plus a typed link.
  The database's unique `(group_id, subject_slug)` is an exact-match backstop only, and a cross-group
  duplicate is neither detected nor merged — the accepted cost. **Do not "fix" the unqualified
  "deduplication is cross-group" wording this replaced by widening the version-target lookup**; that
  would reintroduce cross-group lock ordering and is a design change, not a bug fix.
- **The registry is advisory.** `memory.facets` has no FK to `label` by design (HLD 001 (storage)). The skill
  proposes new labels; it must not treat the registry as a closed set.
- **Blobs are never deleted on the write path.** Content addressing means identical bytes share one
  address, and `IBlobStorage` has no refcount or enumeration. `DeleteAsync` on a redaction orphan can
  destroy content a different version still references. "Orphan" means *drop the DB reference only*;
  leaked-secret purge is out-of-band, not skill work.
- **Programme knowledge is never citable as shipped product behaviour.** Scope is enforced at
  retrieval, but the write path must set the correct scope on the group so the retrieval rule has
  something to enforce.
- **Ticket hierarchy is practitioner-declared only.** Carry explicit set/reparent/remove declarations
  into the authorized capture checkpoint, with exact provider/key identities and explicit expected
  parent. Never infer from spelling, groups, claims, memory links or tracker polling. Memory status
  approval is not hierarchy authorization; read-only findings never invoke the writer.
- **Near-miss tags use only supplied, approved examined evidence.** No extra query, synonym heuristic,
  registry scan, hidden/global vocabulary, automatic broadening or write. UUID/version allowlist and
  exact scope dimension/identifier binding plus grounded caller/skill analysis are mandatory. Original
  API query `tags`/`facetMatchMode` are the only predicate source; matching tags or no relevance evidence
  produce no finding. Evidence approval is not canon: preserve lifecycle status and flag proposed records.

## System Context

The skill is a thin client over the HTTP API (PR #14), which is the only thing that touches PostgreSQL and
blob storage. The skill owns judgement; the API owns mechanics. Semantic subject uniqueness and
write-time AI work remain skill-owned. Exact ticket ownership now also has a trigger-enforced check
under the shared transaction advisory lock, without a normalized ownership table or key rewriting.

```mermaid
flowchart LR
    A[Agent / Human] -->|capture candidates| B[mimisbrunnr-context-memory skill]
    B -->|"set (preflight + one transaction)"| C[HTTP API]
    C --> D[(PostgreSQL)]
    C --> E[(MinIO blob)]
    B -->|"get (cheap fields, drill-down)"| C
```

## Architecture Decisions

- **LADR-005** (2026-09, accepted): Separate read and write execution contexts. *Context:* baseline
  recall may return 200 rows and consume the task's working context; broad shell grants cannot prove a
  read worker cannot mutate. *Decision:* delegate raw recall, expose a read-only client, and enforce
  read/write credentials at the API. Lookup and grounding return bounded cited conclusions plus
  matched-but-not-surfaced disclosure. *Consequence:* aggregate token spend may rise, but main-context
  longevity improves and the read boundary is structural when runtimes keep the write credential absent.

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
  bounded semantic recall, while preflight separately supplies exact subject/ticket facts and
  detects **intra-batch**
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

- **The bundle detector scores claims, not prose.** `atomicity.py` reads the **statement**; a description is a subject label and coordination inside it is not a second claim. A reason clause (`because`, `so that`) and a noun-phrase `and` are one fact, so neither counts as a junction — scoring them made the signal fire on 12/12 candidates of a real batch, including every candidate the detector then called simple. A contrastive junction or a semicolon cannot join anything but two finite clauses, so one is decisive; additive adverbs take two.
- **Atomicity checked three times.** Restated at accumulation (Listen), at the pre-write round, and as
  the `skipped` count in the digest. The check is: one memory = one atomic fact. Bundles split; the
  unprocessable remainder goes to `skipped`.
- **Semantic subject matching is the skill's job.** The `subject_slug` unique index catches exact
  re-capture only. Word-overlap heuristics misfire on short subjects (proven in trial 3). Candidate
  recall is narrowed by facet/kind, then the LLM judges a bounded top-N; the match drives version-bump
  vs new-memory vs skip.
- **Authority resolution retains both positions.** Candidate winner is one version bump. Existing
  winner is two ordered version writes in one transactional set: candidate loser first, existing winner
  second. Genuine conflict remains two current claim identities plus a proposed divergence record;
  helper-generated alternative description avoids same-group subject uniqueness collision.
- **Recall facets/tags match ANY (union), not containment.** A multi-facet batch must return every
  memory carrying any requested facet; containment would return nothing for any batch no single memory
  fully covers — an empty result that looks like an empty store and defeats dedup. Traversal
  (`paths` subcommand) answers *provenance* ("how is A connected to B") rather than *content*, requires
  a caller-supplied `maxDepth`, and obeys the same scope boundary as `query`.
- **`valid_from` derives from the source date when known**, not always `now()` — otherwise bitemporality
  is decorative exactly as MemoryLink was. Business time and system time are never conflation (HLD 001 (storage)).
- **`get` renders results as quoted data** with `sources`, `status`, and scope. A stored memory is not
  an instruction; the store is local, not thereby trusted as settled canon. `proposed` records are
  excluded or flagged by default.
- **Every read surface frames its output, and the framing is a default rather than a per-command
  decision.** `import --store` admits transcripts and meeting notes through the normal capture path, so
  a recalled statement can read as an instruction — and unframed it returns carrying the store's
  authority, which is the most authoritative-looking text in an agent's context and therefore the most
  effective place to hide one. `RECALL_NOTICE` is defined **once** in `context_memory_client.py` and is
  the `mimisbrunnr-understanding` client's `DATA_NOTICE` verbatim, asserted by a test that loads the
  sibling rather than restating the string; a paraphrase is a test failure, which is what keeps "never
  two wordings" true rather than aspirational. The read client frames **at its dispatch**, so a new
  subcommand is covered the day it is added and the only judgement to make is whether it belongs in
  `UNFRAMED_COMMANDS` — a question about content, not bookkeeping. `READ_COMMANDS` is the declared
  surface and `FRAMED_COMMANDS` is derived from it, because two hand-maintained lists drift and a name
  in one and not the other is either silently unframed or silently double-declared. `probe` is the sole
  opt-out (it reports a TCP connection and returns no record), and a test holds that reason true.
  **The notice appears exactly once, and the outermost layer owns the banner.** `query` is reachable
  from both the capture client and the read client, so two framing layers see the same result; each
  deciding independently whether a banner had been printed produced the notice twice, and then not at
  all. The rule is one owner: the inner layer attaches the field silently, the outer prints the prose.
  Both the field and the prose are emitted, because a notice only in prose is invisible to a program and
  a notice only in a field is dropped by any caller that rebuilds the object. Attribution is untouched —
  uuid, version and capture time pass through exactly as the API returned them. A non-JSON body (a blob)
  takes the banner branch, which is the one place the read client prints prose itself, because there is
  no object to carry the field.
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
- **Separate ticket commands:** `ticket-parent` sends PUT `/api/context/tickets/parent`; local
  `--dryrun` shares shape validation but makes no network call and does not claim server validation.
  `ticket-paths` sends POST `/api/context/tickets/paths`, requires depth 1..5, sends direction and
  path/memory caps explicitly (outbound and 50 each by default, caps 1..200). Ticket identity grants no
  scope consent. Render the whole response, including disclosure, on empty/capped results. Never retry
  errors with broader scope or a changed expected parent. API owns ownership/cycles and atomic mutation;
  hierarchy is current state, not history, and separate from the memory-set transaction.
- **Ticket shape guards match deterministic wire limits.** Identity strings max 512 and reason/source
  max 4000 UTF-16 code units, including surrogate pairs for non-BMP characters; NUL/invalid Unicode fail.
  No trimming/truncation. Shared checks apply to write/dry-run and traversal anchors; traversal filter
  strings are capped at 32/64 units. `observedAt` requires extended ISO with uppercase T/Z or colon offset,
  optional seconds and 1..16 fractional digits, offset at most 14:00, valid calendar and UTC years 1..9999.
  Preserve the original timestamp string; local parsing must not round/truncate what goes on the wire.
- **Expected parent precedes no-op, not replay.** The server checks the expectation before comparing
  parent/reason/source/observedAt. An identical state with expected current parent returns
  `changed: false`; replaying an old null expectation after initial set conflicts. No replay token
  exists. Never treat uncertain transport outcomes as successful no-ops or silently alter expectations.
- **Coverage stays qualified:** undeclared upstream hierarchy was not followed; freshness is unverified.
  Ticket paths are not memory provenance paths. No hidden IDs/counts or inferred missing parents.
- **`near_miss_tags.py` is offline stdin/stdout validation, not semantic detection.** The strict payload
  carries scope-bound approval references, frozen `originalQuery`, UUID/version/tags/statements/status/
  actual scope, caller/skill analysis with an exact supporting quote, selected references and disclosure.
  Query `facetMatchMode` alone controls tags (omission means ANY; explicit null/unknown mode fails).
  Legacy `criteria`, independent tag modes and unknown query fields fail; non-tag API fields are preserved,
  not evaluated. References must resolve within approved examined records and match approved actual scope.
  Customer/program scope requires an identifier. Mixed-scope evidence requires its own explicit approval,
  not consent inferred from query or set name. Findings retain actual scope/status and flag proposed
  evidence without dropping or promoting it. It separates observed failed exact predicates from relevance,
  preserves original query/selection/disclosure, sorts by UUID/version, and qualifies absence/caps/non-tag
  exclusion. Rejects input above 1 MiB, 200 records/analyses/references or 200 tags per list without
  partial output. These are local safety limits, not HLD-005 dossier caps; authorization truth and
  semantic quality remain skill-owned. Full dossier and tag identity/synonyms remain unimplemented here.

## Test References

- **Committed L0 harness (CI-gatable):** `.agents/skills/mimisbrunnr-context-memory/tests/run_tests.py` — stdlib
   `unittest` (no external runner). Unit-tests `redact.py` secret containment; `atomicity.py` bundle
   detection; `context_memory_client.py` path, credential, loopback and redirect guards; `deepsearch.py`
   caps, deduplication and omission disclosure; `divergence.py` composition, pair idempotency and
   recursion rejection; `authority.py` ordered version composition; read-client/MCP fail-closed
   capability boundaries; project/Copilot agent registrations; ticket transport/guards/dry-run/lossless
   disclosure; blinded semantic-fixture emission/scoring; `TransportFailureTests`, which drives a **real
   stalled socket** to prove a hung store is classified `timed-out` rather than escaping as a
   `TimeoutError` traceback, and that a refused connection stays `unreachable` (the control that makes
   the first assertion meaningful — a fix classifying every transport error as `timed-out` passes one
   and fails the other); `RecallFramingTests`, which recalls a record whose statement is a verbatim
   prompt injection through every registered read subcommand **via the client's real `main()`** and
   requires the shared notice plus intact attribution on each — driving the entry point rather than the
   framing helper, because an earlier version passed with the whole fix reverted; and
   `near_miss_tags.py` schema/scope/basis/
   bounds/output/no-I/O guarantees.
   Run: `python3 -B .agents/skills/mimisbrunnr-context-memory/tests/run_tests.py`. The PR gate runs it and
   `tests/measure_cost.py` (reproducible structural cost evidence) in the same step.
- **Deterministic near-miss fixtures:** `tests/fixtures/near_miss_tags.json` exercises grounded mismatch,
  exact match, ANY overlap, irrelevant evidence, unsupported plausible synonym and empty tags. Its evidence
  contains the original API query, scope-bound approval entry, and explicit approved lifecycle status.
  Harness additionally checks default ANY/explicit ALL against API-shaped queries, duplicate mode rejection,
  mixed-status/scope findings, scope-binding failures, empty evidence, case-sensitive predicates, invalid
  UUID/version/scope/basis, executable stdin/stdout, rejection without partial output, preserved query/
  selection/disclosure, stable order and zero network/file access. Ticket tests include UTF-16 boundaries,
  NUL/Unicode rejection and timestamp wire/calendar/offset guards in transport and dry-run. These tests
  validate plumbing, not LLM judgement or live API behavior.
- **On-demand LLM-eval fixtures, scored in CI but model-judged only on demand:**
  `.agents/skills/mimisbrunnr-context-memory/tests/fixtures/scenarios.json` plus `score_fixtures.py`. Authored
  positive/negative scenarios for the semantic-dedup, atomicity, link and divergence stages, scored
  for recall AND precision against a countable expected-verdict set. Give the model only
  `score_fixtures.py --emit-model-input` output; the source fixture contains expected answers and is
  scorer-only. A run where the model reads `scenarios.json` is circular and invalid evidence.
  **The judgement itself is not CI-gated, but the harness is:** `run_tests.py::SemanticFixtureTests`
  invokes the scorer twice and the PR gate runs `run_tests.py` — once to prove the blinded emitter
  withholds `expected`/`note`/`axis`, and once to assert the **committed** verdicts file scores exactly
  recall 1.0 / precision 1.0. So a stale expectation is not inert: it is *certified*. A fixture whose
  expected verdict contradicts the shipped contract will not fail a test, it will make the recorded run
  look perfect.
- **Verdicts pair by `id`, not by position.** The emitter emits each scenario's `id` and the scorer
  refuses a verdicts file whose entries carry no id unless `--allow-legacy-positional` is passed.
  What keeps a run blinded is the emitter withholding `expected`, `note` and `axis`, which the
  harness asserts. The id is emitted for pairing; do **not** restate that as "an identifier is not an
  answer" — in this fixture set two ids (`s4-cross-group-match-is-not-a-bump`,
  `s8-near-miss-negative`) name their own expected verdicts, so anyone reading the repository can see
  the answers. That is a transparency property of a *committed* evidence file, and the honest handling
  is to record it rather than to build id-scrubbing for the blinded input, which is already sound.
  Pairing matters because positional pairing meant that
  inserting, deleting or reordering a scenario silently misaligned every verdict after it, and the
  1.0/1.0 assertion above would then have certified the wrong verdicts against the wrong scenarios
  **with no test failing**. `test_reordering_the_verdicts_does_not_change_the_score` is the property
  positional pairing could not have. **Appending is no longer the only safe edit** — adding, inserting
  or reordering is fine; a new *run* is still required, since a run's verdicts are a record of one
  model's judgements on one day.
- **Each run is a new dated file** (`model-verdicts-<date>.json`, with a qualifier when one day holds
  two runs); a previous run is a record of what the model said that day and is never edited in place,
  so a superseded run stays available for re-scoring. A dated run is only re-scorable against the
  fixture it was taken against, so freeze that too (`scenarios-<date>.json`) — otherwise re-scoring a
  ten-verdict run against today's fourteen-scenario fixture is a length error, not a measurement.
  `scenarios-2026-09-29.json` is the frozen set behind the 0.9 / 0.8333 and 1.0 / 1.0 historical runs.
- **Every scenario declares its `axis`** — `recall_positive`, `precision_negative` or `not_dedup` — and
  the scorer and harness both refuse a set with fewer negative controls than positive pairs. Declared
  rather than inferred from the expected verdict word, because a scenario expecting `new_memory` for a
  reason unrelated to matching would otherwise inflate the negative count, and a matcher that matches
  nothing scores perfect precision. `axis` is withheld from the blinded input: it would tell the model
  which way the pair is meant to fall.
- The skill's test approach is specified in `docs/hlds/002-context-memory-write-pipeline/nfrs/NFR-02-deduplication-accuracy.md`. These
  are not this repo's L0/L1/L2 tiers, which apply to the C# API (PR #14).

## Requirements

Approved (2026-09-12) implementation plan for making this contract executable
against the HTTP API (PR #14). No C# changes. The plan detail and decision set live in the gitignored working
 specification. Key decisions: semantic dedup composed from
`/query` recall (facets+kind, no free-text, `includeProposed:true`, `limit:200`) + LLM judgement,
with `/preflight` as exact-match backstop + intra-batch + ticket-uniqueness only (amends HLD 002 (write pipeline)'s
"Write preflight" API row); digest renders `skipped(atomicity)` separately from `skipped(duplicate-link)`;
redaction detector is a stdin→stdout fingerprint script reporting rule names only; 20-candidate cap
 as a static configurable setting. Divergence creates a proposed record plus transactional
 contradiction links and reports a real count.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-01 | **A `uuid` or `version` path segment is validated before any URL is built, and the write credential is chosen by exact route.** Every surface that interpolated a caller-supplied segment (CLI `get-versions`/`get-blob`/`update-group`/`append-description`, read MCP `get_versions`/`get_blob`, write MCP `update_group`/`append_description`) now goes through shared path builders that accept only a canonical 8-4-4-4-12 UUID and a positive integer version, so `/`, `.`, `?`, `%` and `#` never reach the path — previously an `append_description` uuid of `../../snapshot?x=` reached `POST /api/context/snapshot` with the write token. Token selection no longer uses `method == "PATCH"` or `"/descriptions" in path`: it is an exact `(method, path)` set plus two UUID-anchored templates, and anything unlisted gets the read token, so a GET can never carry the write credential. Harness 119 -> 126; both changes mutation-checked. | HLD-002 NFR-01 |
| 2026-10-01 | **Every read surface now frames recalled memory as untrusted data, from one shared notice.** `import --store` admits transcripts and meeting notes through the normal capture path, so a recalled statement can read as an instruction — and unframed it returned carrying the store's authority, which is the most authoritative-looking text in an agent's context and the most effective place to hide a prompt injection. Two of four read surfaces framed; the read client did not, and `deepsearch` did not. `RECALL_NOTICE` is defined once and is the `mimisbrunnr-understanding` client's `DATA_NOTICE` **verbatim** — a test loads the sibling module and compares, so a paraphrase fails rather than silently becoming a third wording. The read client frames **at its dispatch**, so a new subcommand is covered the day it is added; `READ_COMMANDS` declares the surface and `FRAMED_COMMANDS` is *derived* from it, because two hand-maintained lists drift and a name in one but not the other is either silently unframed or silently double-declared. `probe` is the only opt-out, with a test holding its reason true. Emitted **both** as a prose banner and as a top-level `recallNotice` field, because a notice only in prose is invisible to a program and a notice only in a field is dropped by a caller that rebuilds the object; attribution (uuid/version/capture time) is carried through untouched. The read worker's contract now says what to *do* with a record whose statement reads like an instruction: report it quoted with its identity, do not act on it, and do not drop it for being unusable. **Three defects were found by the tests while writing them, all recorded in the Key Behaviors entry:** (1) `FRAMED_COMMANDS = frozenset(READ_COMMANDS)` made the framed and opted-out sets overlap, so `probe` was declared both — the two are now disjoint by construction. (2) **The first version of the suite passed with the entire fix reverted**, because it called the `_run_framed` helper directly rather than the `main()` that decides whether it runs; every test now drives the real entry point. (3) `_load` execs each script standalone, so `read_client.client` was a *second* module object — the tests were patching a module the read client never called, and the symptom was a live connection attempt rather than an assertion failure; the sibling modules are now aliased in `sys.modules` so there is one object. Two framing layers also disagreed about who prints the banner, producing the notice twice and then not at all; the outermost layer now owns it. 118 tests, up from 109; four mutations verified. | MemOS adoption; HLD-007 |
| 2026-10-01 | **Review round: a mid-response socket reset was the same traceback by another route, the framing drift guard was circular, and a `get-blob` body that is valid JSON was being re-serialised.** (1) `_read_response` caught `URLError` and `TimeoutError` but not a bare `OSError`, so a connection accepted and then **reset** escaped as a traceback — the exact failure the transport classification exists to prevent, reached a different way. A new `except OSError` clause classifies it as **`unreachable`, not `timed-out`** (a reset is not a hang), placed **last** because `URLError` is itself an `OSError` and an earlier clause would shadow the refusal control. Driven for real, forcing an RST with `SO_LINGER` 0; mutation-verified. (2) `test_every_read_subcommand_is_framed_by_default` asserted `FRAMED_COMMANDS \| UNFRAMED_COMMANDS == READ_COMMANDS`, which is **true for any values of either set** because `FRAMED` is *defined* as `READ - UNFRAMED` — so it stayed green with the entire MCP surface unframed, the silent pass its own comment claims to prevent. It now cross-checks the CLI's surface against `read_mcp.TOOLS`, an independent declaration. (3) `_parse_framed_json` treated any output containing `{` as a JSON candidate, so a `get-blob` body that happens to be valid JSON was parsed, re-indented and merged with `recallNotice` instead of returned byte-faithful; `RAW_BODY_COMMANDS` makes the discriminator the **command**, not the text. (4) Dead `_printed_json` deleted — no caller, and its "banner is the first line" assumption is exactly what `_split_banner` replaced. (5) Two comments describing banner-suppression detection were corrected: single emission comes from `_run_framed` **discarding the captured buffer**, and `banner=False` has no call site — the comments named a mechanism that does not exist. 121 tests, up from 118. | PR review |
| 2026-10-01 | **A hung store raised a bare `TimeoutError` traceback instead of a classified error, and `timed-out` is now distinct from `unreachable`.** `urllib.error.URLError` covers the connect phase, but a read that times out on an already-established socket raises `TimeoutError` — a subclass of `OSError`, so the `URLError` handler never saw it. Reproduced against a real stalled socket before fixing: `_request` and `cmd_get_blob` both died with `TimeoutError: timed out` and a traceback, which is the shape a hung store produces and the case an agent most needs to recognise. The two outcomes are kept apart deliberately: `unreachable` means nothing is listening, `timed-out` means something accepted the connection and went quiet, and the caller's response differs. Collapsing them reports a slow or overloaded store as a dead one. Four tests drive a real socket rather than a mock — the defect is that the timeout arrives on an unanticipated path, so a mocked `URLError` reproduces the case that already worked and passes against the unfixed client. Three mutations verified, including the tempting wrong fix (classify every transport error as `timed-out`, which passes the hang test and fails the refusal control). **The duplicated `except` block was the clean-code defect:** `cmd_get_blob` carried its own copy of all four handlers, which is how a fix lands in one and misses the other — so both sites now share `_read_response`, and a fourth mutation reintroducing the copy is caught. `SKILL.md` gained a **Transport Failures** section, because the skill documented no error states at all and the agent is the party that must act on them; it names the three outcomes and states that `timed-out` must never be read as an empty result, which is the expensive direction — indistinguishable from a correct answer. 109 tests green. The budget itself (`HTTP_TIMEOUT`) is unchanged at 30s; this is the correctness half of the deadline work, not the cap. | MemOS adoption |
| 2026-10-01 | **The digest's human step named an action, and `propose-label` gained a threshold.** "Review the digest" was not an instruction anyone could perform, so the one moment a person audits what the model decided had no stated task. `## Finalization Output` now carries a table mapping the shape of the digest to the one check to name — held-back atomicity splits, proposed gated kinds, diverged pairs, above-threshold label proposals, or a clean batch — with the rule that **one** check goes in the digest adjacent to its counts, because a list of five is the same as "review". The advisory-registry guardrail gained the same three-occurrence arithmetic `--promote` uses: a label seen once is a word, three independent captures is vocabulary, and proposing below that requires naming the near-synonym it duplicates. Wording only, no script change, so no test tier applies; the committed L0 harness is unchanged. | ICM adoption |
| 2026-09-29 | **The redaction gate closed, and it is a gate on recognition rather than on secrets.** `set` now scrubs every free-text field through `redact.scrub_set_payload` before the request is built and refuses the write if the scrubber cannot run, so `redact.py` is no longer a tool an operator may skip and the gate cannot fail open — a write that cannot be scrubbed is not written. The boundary is stated rather than implied: the rules are fixed-shape fingerprints (`AKIA…`, `gh[pousr]_…`, PEM blocks, `secret=`/`token:`/`password=` assignments), so a value matching no rule still passes, and "recognisable secrets are gated" is the claim, never "sensitive material never reaches storage". `scenarios.json` gained `axis` per scenario (2 recall positives / 5 precision negatives, asserted by the harness), the frozen `scenarios-2026-09-29.json` and the balanced run were added, and verdicts now pair by `id` with `--allow-legacy-positional` reserved for superseded runs. | HLD-002 LADR-03, NFR-02 |
| 2026-09-29 | **The semantic-dedup fixture set still encoded the overturned rule, and CI was certifying it.** Fixture `s4-cross-group-version-bump` expected a same-subject match in `g-99` to yield `version_bump` against `m-002` — which the shipped group-scoped identity forbids, since a foreign `uuid` target is a `404`. The committed verdicts run recorded the model doing exactly that, and `run_tests.py::SemanticFixtureTests` asserts the committed run scores recall **and** precision of exactly 1.0, so the defect was certified rather than caught. Re-scoring that run against the corrected expectation gives 0.9 / 0.8333. The fixture is now `s4-cross-group-match-is-not-a-bump` expecting `new_memory` (a new memory plus a typed link to the twin), a new `model-verdicts-2026-09-29.json` carries the corrected run at 1.0 / 1.0, and the **09-17 verdicts file is left on disk unedited** — it is the record of that run, not a configuration, so it stays available for re-scoring. Two claims in this file were also wrong and are corrected above: the fixture set is **not** "not CI-gated" (the scorer is invoked twice by `run_tests.py`, which the PR gate runs), and `score_fixtures.py` matches verdicts to scenarios by **position**, so inserting or reordering a scenario silently misaligns them and the 1.0/1.0 assertion then certifies the wrong verdicts against the wrong scenarios. | HLD-002 NFR-02 |
| 2026-09-28 | **Memory identity decided group-scoped**, with HLD-002 amended to match, so the two files that stated the overturned rule as rationale now state the shipped rule. The **behaviour contract was already correct** and is unchanged: recall reads across groups, a match inside the writing group is a version bump, a cross-group match is a new memory plus a typed link (the 2026-09-27 rows already qualified every `uuid`-emitting site). What changed is the two rationale sentences — the non-negotiable "Deduplication is cross-group … a per-group check misses it" and the `SKILL.md` Listen-stage restatement, which asserted a within-group check as a defect. They now separate the two things the old wording conflated: the read is cross-group, the *versioning target* is group-scoped, and a cross-group duplicate is an accepted cost rather than a bug. The non-negotiable gained an explicit "do not widen the version-target lookup" clause, because the old wording reads as an instruction to do exactly that. The deliberate triple restatement of the rule is preserved — only its wording changed. | HLD-002 LADR-01, LADR-04 |
| 2026-09-27 | Qualified the cross-group dedup restatement at the `SKILL.md` semantic-dedup **Judge** step (the only site that instructs the model to emit a recalled row's `uuid` as a `version_bump` target) with the same group rule the `set` body states: a same-subject match is a version bump **only inside the request's `groupUuid`**, and a cross-group match is a new memory plus a typed link, because a foreign `uuid` target is a `404` (group-scoped version-target lookup in `SetMemories.cs`). The recall query that feeds that step is deliberately cross-group, so the unqualified wording produced a guaranteed `404`. | `SKILL.md` |
| 2026-09-27 | Qualified both cross-group dedup restatements (`SKILL.md` stage-3 item 1 and the write-pipeline table's stage-3 row) with the group rule already stated in the `set` body: a same-subject match is a version bump **only inside the writing group**, and a cross-group match is a new memory plus a typed link. Unqualified, both restatements instructed a `uuid` target the server 404s (group-scoped version-target lookup in `SetMemories.cs`). Restatements kept — they are the drift countermeasure, not redundancy. | `SKILL.md` |
| 2026-09-26 | `SKILL.md`/`README.md` re-laid out switches-first: the switch table now opens each file ahead of the H1, matching `mimisbrunnr-vitsmunir-dump` and `mimisbrunnr-understanding`. In-body pointers updated; no behavioural or contract change. | PR #108 |
| 2026-09-20 | Replaced the outdated universal Codex custom-agent limitation with the current `.codex/agents/*.toml` capability and kept the actual safety gate on verified effective MCP/tool grants; no runtime configuration was added. | [OpenAI Codex subagents documentation](https://learn.chatgpt.com/docs/agent-configuration/subagents) |
| 2026-09-20 | The "sole interface to the store" claim retired from the five remaining living docs (`SKILL.md` prose + description, `README.md`, `.agents/skills/README.md`, `docs/wiki/architecture.md`, `AI_DEVELOPMENT_AGENTS.md` root table) — `mimisbrunnr-understanding` is a reader + conditional importer, so this skill is the sole **authority on the write path**, not the sole interface. The prior row's narrowing is now fully propagated instead of landing in `AGENTS.md` only. | PR #86 review |
| 2026-09-19 | Narrowed "sole interface" to "sole authority on the write path" and noted the read/load sibling (`mimisbrunnr-understanding`) as a reader + conditional importer that hands `--store` material to this capture path. | HLD-007 LADR-03; BRD-003 |
| 2026-09-17 | Test References corrected: the PR-gate skill step runs both `run_tests.py` and `measure_cost.py`, not `run_tests.py` alone. | `.github/workflows/pr-gate.yml` |
| 2026-09-17 | Both stdio MCP servers answer malformed JSON with a JSON-RPC parse error (-32700) and non-object messages with Invalid Request (-32600) instead of crashing the worker; parsing is shared through `memory_read_mcp.respond`. | code review |
| 2026-09-17 | Delivered memory-read/memory-write contracts, read-only client, bounded deepsearch, divergence composition/loop guard, createUuid payloads, cost evidence and deterministic PR gate. | HLD-002 closure |
| 2026-09-17 | Added executable authority resolution, exact-subject divergence storage, and Copilot custom-agent registrations; Codex custom-agent limitation documented. | BR-10 closure |
| 2026-09-17 | Approved delegated read/write execution, API-backed capability separation, bounded deep search, transactional create UUIDs and proposed divergence contract. | HLD-002 delta closure |
| 2026-09-16 | SKILL.md recall rationale updated for stemmed full-text recall: `/query` free-text is now AND-of-all-lexemes under the `english` configuration - stemming forgives inflections, not sentence structure, so natural-language free-text still defeats recall. | HLD-001 storage delta |
| 2026-09-15 | Replaced unmerged near-miss criteria schema with frozen original API query (`tags` + sole `facetMatchMode`, default ANY); bound approved UUID/version evidence to actual scope and retained status/scope with explicit proposed flag. Added shared ticket UTF-16 length/NUL/Unicode guards and wire-compatible timestamp validation without normalization. Updated fixtures/docs; Python harness 42 passing tests including positive/negative offline regressions. No retrieval, compatibility shim or C# changes. | HLD-005 LADR-10/13, NFR-04; HLD-002 LADR-08 |
| 2026-09-14 | Synced documentation to strict expected-parent-before-no-op behavior, no operation replay token, selected capped path endpoint/anchor memory association and trigger-backed exact ownership. No scripts changed by this sync; ticket performance gate remains open and targeted tests do not imply release acceptance. | HLD-002 LADR-08; HLD-003 LADR-08 |
| 2026-09-14 | Added exact-identity `ticket-parent` PUT with explicit nullable parent/expectedParent, source metadata and no-network local dry-run; bounded `ticket-paths` POST preserves the complete response/disclosure. Documented declaration-only checkpoint/approval rules, conflict handling, scope consent and partial/stale hierarchy coverage. Added offline bounded `near_miss_tags.py`, strict approved-evidence schema, grounded analysis/observed mismatch separation, unchanged selection/disclosure, positive/negative fixtures and transport/output/no-I/O tests. Python harness: 34 passing tests; live API integration and LLM relevance quality not measured. HLD docs owned by parallel work left untouched. | HLD-002 LADR-08; HLD-003 LADR-08; HLD-005 LADR-10, NFR-04 |
| 2026-09-14 | SKILL.md gained `## Request Bodies` — the wire contract per subcommand, previously documented nowhere and guessable only from `/openapi/v1.json`. Corrected `facetsMatchMode` → `facetMatchMode` (the documented spelling was rejected as an unknown property). `atomicity.py` scores the statement, not description+statement, and no longer counts reason clauses or noun-phrase `and` as claim junctions. | e2e-dogfood |
| 2026-09-14 | Brand-prefixed to `mimisbrunnr-context-memory`. | |
| 2026-09-13 | The API rejects unknown request fields as a 400, so a misspelled payload field never silently defaults; `update-group` accepts `tickets` as an additive, idempotent, cross-group-unique merge. | BUG-03 |
| 2026-09-13 | `/query` recall: facet/tag match is ANY by default (rows carrying any requested facet), containment (`all`) opt-in — matches the recall union the dedup step needs. `paths` subcommand added (bounded multi-hop traversal, `maxDepth` required, scope enforced); SKILL.md gained `## Traversal` guidance on when to traverse vs query. | BUG-02, BUG-04 |
| 2026-09-10 | Created — contract for the sole interface to the context-memory store; fixed write pipeline; cross-group dedup, atomicity and secret-redaction ownership. | HLD 002 (write pipeline) |
| 2026-09-10 | Contract-coherence pass. Pipeline numbering pinned to five stages with **preflight as stage 1** (SKILL.md previously specified four, starting at redact). Approval gating resolved to *write-as-`proposed`* — SKILL.md previously said "do not write", which contradicted this file and the retrieval rule that excludes `proposed` records. Digest reclassified as a post-write receipt with `--dryrun` named as the only pre-write veto point (LADR-002 previously implied a veto round that `set`'s single transaction cannot provide). Intra-batch collision, source-date `valid_from`, and the summary model/prompt stamp added to SKILL.md, which the executing agent reads. | HLD 002 contract review |
| 2026-09-12 | `## Requirements` added — approved implementation plan (skill executable against the HTTP API; no C#). Key decisions recorded (semantic dedup via `/query` recall + LLM judgement; digest `skipped` segregation; redaction rule-name digest; 20-cap static setting; divergence non-collapse). | PR #17 |
| 2026-09-12 | **Skill implemented (no C#).** Added `scripts/context_memory_client.py` (14 subcommands, base-URL + health probe, `MAX_CANDIDATES`=20 cap, `--dryrun`), `scripts/redact.py` (stdin→stdout, rule-name digest, true-positive rules), `scripts/atomicity.py` (conservative bundle detector). SKILL.md gained deterministic component and scope contracts; digest skip segregation added. Test home: committed L0 harness plus on-demand fixtures. CI wiring landed during HLD-002 closure on 2026-09-17. | PR #17 |
| 2026-09-30 | **The scorer measured accuracy and printed it under the name recall, and the two historical figures it produced were wrong.** `recall = correct / total` over all fourteen scenarios, so a matcher that collapsed *nothing* scored 0.857; a matcher that collapsed everything scored 0.2 precision on its own claims but the old precision divided by every dedup-class scenario whether or not the model claimed anything on it, so five scenarios that cannot over-merge diluted the denominator and flattered the score. Recall is now over the scenarios declared `recall_positive`, precision over the collapses the model actually claimed, and `accuracy` is reported under its own name because that is what the old `recall` was. A fixture declaring no positive pair now yields `recall: null` rather than `0.0` — a frozen pre-`axis` fixture cannot measure recall, and printing 0.0 there invites "the model had zero recall" where the truth is "this was never a recall measurement". **Two quoted figures are corrected rather than left to be re-derived:** the 2026-09-17 run is `accuracy 0.9 / precision 0.5` and was previously quoted as `recall 0.9 / precision 0.8333`. The 2026-09-29-balanced run is 1.0 / 1.0 / 1.0 either way, which is *that run re-scored* under corrected denominators rather than a fresh run — correcting an instrument does not re-ask the model, and the evidence document keeps the two facts separable. Second: the balance refusal was reachable past, because `--fixtures` switched it off together with the positional pairing — so any frozen fixture could be scored with no negative controls at all, the exact condition NFR-02 exists to prevent, one extra flag away. Balance is a property of the fixture, not of the run, and is now checked for every run that is not a re-score of a superseded dated one. Third: **the "an identifier is not an answer" claim is removed rather than repeated.** It was the stated justification for emitting `id` in the blinded input, and it is not true of this fixture set, where `s4-cross-group-match-is-not-a-bump` and `s8-near-miss-negative` name their own expected verdicts — so anyone reading the repository can see the answers. What keeps a run blinded is the emitter withholding `expected`, `note` and `axis`, which the harness asserts. No id-scrubbing was added: that claim was a transparency property of a committed evidence file, misfiled as a property of the input. Harness 98 -> 105, each of the three changes mutation-checked. | HLD-002 NFR-02 |
