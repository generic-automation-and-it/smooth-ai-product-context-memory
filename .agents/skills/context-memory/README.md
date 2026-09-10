# context-memory — Intent & Token-Usage Review

> Companion notes to [`SKILL.md`](./SKILL.md). Explains what this skill is for and — because
> **R13 makes every write an LLM call** — where it genuinely costs tokens and where it saves
> them. Mirrors the honesty of `ai-brain-dump/README.md`: the savings are real but not where
> you might expect.

## Intent review

The skill is the **sole interface** to the context-memory store. It captures candidate facts
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

…plus a fixed write pipeline inside the write (redact → dedupe/derive-links → atomicity → write) and a
retrieval (`get`) path that returns cheap fields by default and touches the blob only on drill-down.

## Where the tokens actually go

The dominant cost is **R13**: every write is an LLM call to generate `content_summary` and keywords
(from the blob content), plus the semantic dedup and link-derivation judgements. Retrieval is cheap by
default. Get the cost model wrong and the skill becomes more expensive than the problem it solves.

### Where it genuinely costs

- **Every `set` costs an LLM call per fact for summary + keywords.** This is the big one. A batch of
  N facts costs ~N LLM judgement calls. There is no way around it — the body is in blob storage, not
  indexable (ADR-0002), so the summary is the search surface. This is the price of the design, not a bug.
- **Semantic dedup and link derivation are LLM judgements** on the pre-write round. Narrowed by
  facet/kind to a bounded top-N first, so the judgement is over candidates, not the whole store.
- **`--dryrun` costs the same as a real write** (same pipeline, no persistence). It is the inspection
  cost, deliberately — it buys confidence that a non-trivial batch is right before committing.

### Where it saves

- **Retrieval is cheap by default** — `get` returns the cheap fields as an array and drills into the
  blob only on demand. The consuming model judges relevance from compact metadata instead of pulling
  full bodies. This is the context-economy payoff (§6.8 of the design): the main session holds
  conclusions, never raw material.
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
| `--dryrun` | **Same as a real write** | Full pipeline, no persistence. Costs the LLM judgements but writes nothing. Safe inspection of a non-trivial batch. |
| `--approve` | **No extra cost, narrower gate** | Skips the approval gate for `rule`/`nfr`/`decision`. Saves a human round-trip at the cost of writing canon without review — the "ask about what is not reversible" rule. |

**Bottom line:** writes are expensive by design (R13) and cheap by default for reads. The skill's value
is not that it is cheap — it is that it is the *only* way to make real, long-lived memory, and it makes
retrieval cheap. Use `--dryrun` (or a plain `set`) deliberately; skip `--approve` unless the human has
explicitly confirmed.
