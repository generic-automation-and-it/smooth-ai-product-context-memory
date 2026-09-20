# mimisbrunnr-context-memory — Intent & Token-Usage Review

> Companion notes to [`SKILL.md`](./SKILL.md). Explains what this skill is for and — because
> **R13 makes every write an LLM call** — where it genuinely costs tokens and where it saves
> them. Mirrors the honesty of `mimisbrunnr-vitsmunir-dump/README.md`: the savings are real but not where
> you might expect.

## Intent review

The skill is the **sole authority** on the context-memory write path. It captures candidate facts
byproduct-style during work and writes them at an explicit end-of-task checkpoint. Its whole value is
in the **write path**: it performs the semantic work the database cannot express as constraints —
cross-group deduplication, link derivation, secret redaction, atomicity checking, and summary/keyword
generation. It is not a passive logger; it decides, per candidate, whether this is a new memory, a
version bump, a divergent claim, or a skip.

It has 4 phases:

1. **Initialize** (resolve the group from ticket/repo/initiative/scope)
2. **Listen** (accumulate candidates silently, never write)
3. **Compare-or-Clarify** (the bounded pre-write round: cross-group dedup + link derivation + ticket-uniqueness)
4. **Write** (the explicit `set` checkpoint)

…plus a fixed five-stage write pipeline (preflight → redact → dedupe/derive-links → atomicity → write)
and a retrieval (`get`) path that returns cheap fields by default and touches the blob only on drill-down.
Phase 3 above **is** pipeline stage 1 — the phases and the stages overlap rather than nest, which is why
the pipeline table in `SKILL.md` is the canonical numbering.

Rule-resolvable disagreement uses ordered versions: if existing claim remains authoritative, incoming
loser is recorded as history before existing winner is restored as current in same transactional set.
Genuine conflict keeps both claims current under separate identities and adds proposed divergence record.

## Where the tokens actually go

R13 requires logical summary and keyword judgement for every fact, but that does not imply one provider
invocation per fact. Harnesses may batch several logical judgements into one invocation. Cost evidence
therefore records provider/agent invocations, logical judgements, candidates inspected, HTTP/blob I/O,
and exposed token counts separately.

### Where it genuinely costs

- **Every `set` performs summary and keyword judgement per fact.** Provider invocation count depends
  on harness batching and cannot be inferred from fact count.
- **Semantic dedup and link derivation are LLM judgements** on the pre-write round. Narrowed by
  facet/kind to a bounded top-N first, so the judgement is over candidates, not the whole store.
- **`--dryrun` costs the same as a real write** (same pipeline, no persistence). It is the inspection
  cost, deliberately — it buys confidence that a non-trivial batch is right before committing.

### Where it saves

- **Retrieval is bounded by default.** `memory-read` consumes candidate rows in isolated context and
  returns cited conclusions plus omission disclosure; blobs remain drill-down only.
- **It replaces ossified prompt re-explanation.** The cost that recurs every session is re-explaining
  context to a stateless model. A retrieved memory carries the reasoning forward without re-derivation.
  This is the amortized saving — paid at write, recovered across many reads.
- **It makes byproduct capture work. There is no separate maintain-the-memory chore.** Capture happens
  during the work that is already happening; the only extra cost is the checkpoint write. If upkeep were
  a separate task it would decay and the store would die — that is the cost the skill avoids, not a
  token cost but a decay cost.
- **Cross-group dedup prevents store bloat.** One subject, one memory (versioned), not N near-identical
  records. Each avoided duplicate is an LLM call and a retrieval-confusion avoided downstream.

### Where it does *not* save — the caveat

- **Aggregate token spend may rise.** Read and write agents each establish context. Gain is main-context
  longevity and enforced read capability, not guaranteed lower total tokens.

- **A wrong dedup decision is expensive and silent.** If the skill misses a semantic match, it writes a
  near-duplicate. If it falsely matches, it version-bumps a different subject and rewrites canon. Both
  are cheap at write time and costly to untangle later. This is why semantic subject-matching is the
  top write-path risk and why it is restated three times in SKILL.md.
- **The stamp that enables bulk regeneration also costs.** The model identifier and prompt version are
  stored per summary so a bad summary batch can be regenerated — but the regeneration is itself N LLM
  calls. The stamp is insurance that costs when used.

## Switches & cost trade-off

| Switch | Cost impact | Why |
|--------|-------------|-----|
| _(none)_ | **Baseline** | Silent capture; write only at checkpoint; no approval override. |
| `--dryrun` | **Same as a real write** | Full pipeline, no persistence. Costs the LLM judgements but writes nothing. The **only** pre-write inspection point — a plain `set`'s digest arrives after the transaction has committed. |
| `--approve` | **No extra cost, narrower gate** | Writes `rule`/`nfr`/`decision` as `approved` rather than `proposed`. Saves a human round-trip at the cost of canon becoming citable without review — the "ask about what is not reversible" rule. Without it the fact is still stored, just not yet citable. |
| `--deepsearch` | **Bounded opt-in** | Adds four keyword passes of 25 and five depth-one traversals of 20, capped at 400 unique UUID/version candidates. Reports saturation and possible omissions. |

**Bottom line:** writes are expensive by design (R13) and cheap by default for reads. The skill's value
is not that it is cheap — it is that it is the *only* way to make real, long-lived memory, and it makes
retrieval cheap. Reach for `--dryrun` when a batch is large or unfamiliar — paying the pipeline twice is
cheaper than untangling a wrong dedup decision. Skip `--approve` unless the human has explicitly confirmed.
