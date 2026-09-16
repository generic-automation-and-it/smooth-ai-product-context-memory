# AGENTS.md - Cross-product linked context memory (BRD)

AI Context: BRD for the context-memory product. Updated: 2026-09-13

## TL;DR

The business authority for the whole product: what it must do and why, in [README.md](./README.md).
It is deliberately technology-free — every "how" lives in the HLDs under
[../../hlds/](../../hlds/). `BR-NN` identifiers in this document are the acceptance vocabulary the
HLDs and their LADRs justify themselves against.

## Non-Negotiables

- **Never add technology, data structures or implementation detail to the BRD.** The document opens by declaring it contains none. A schema, an API shape or a library name here makes business intent unreviewable without technical review, which is the failure the BRD/HLD split exists to prevent.
- **Authority runs BRD → HLD, never back.** When implementation contradicts a `BR-NN`, that is a business decision to escalate — not a doc-sync task. Editing the BRD to match shipped behaviour destroys the only record of what was actually asked for.
- **Never renumber or reuse a `BR-NN`.** Numbers are cited from HLDs, LADRs, success measures and risks. Amend in place or append; a reused number silently redirects every existing citation.
- **The HLD set is listed in three places — the `Related` header row, §11, and the HLD's own `AGENTS.md` back-reference.** Adding an HLD under `../../hlds/` means updating all three; one missed place makes the BRD look complete while it is not. **An HLD owned by a sibling BRD is listed in that BRD's three places, not this one** — this document then carries only a pointer to the sibling BRD (see [BRD-002](../002-contextual-export/)).
- **Single-user is a decision, not a missing feature.** §4 states the reasoning: an intelligence layer over shared content makes pre-existing over-broad permissions easier to exploit, so remaining single-user avoids the class of problem rather than solving it. Do not propose auth, tenancy or sharing as a gap.
- **§5 out-of-scope rows carry reasons, not a backlog.** Each excluded item was rejected on stated grounds. Treating one as future work reverses a decision without recording that it was reversed.

## System Context

One BRD governs one product. Below it sit four HLDs — storage, write pipeline, graph edges, recall
feedback — each an independent design that must trace back to at least one `BR-NN`. A sibling BRD
([BRD-002](../002-contextual-export/)) extends the same requirement space for bulk export and owns
HLD 005. The BRD is written for a business reader: it names no store, no framework and no interface,
so it can be validated against the practitioner's actual working problem rather than against the code
that exists.

Its reasoning was accumulated through recorded braindump sessions rather than drafted in one pass,
which is why §2 states the problem in three independently-observed forms and §6 attributes several
requirements to specific prior-art failures. Those sessions live outside version control
(`.context/braindump/`, gitignored, per-workspace) and may not be present in a given checkout — do
not treat their absence as the reasoning being undocumented, and do not cite them from committed
documents.

## Key Behaviors

- **`Accepted when:` clauses are the contract, not the prose above them.** The paragraph explains why a requirement exists; the clause is what a reviewer tests. An HLD satisfying the paragraph but not the clause has not satisfied the requirement.
- **BR-01 outranks everything else.** It is marked "the single most important requirement" because every prior attempt failed on it. Any design introducing scheduled upkeep fails the BRD regardless of how well it satisfies the other sixteen.
- **BR-09 is bitemporality stated in business language.** "Distinguish a correction to a record from a change in the world" is the same requirement as `created_on` beside `valid_from` in HLD 001 — do not read it as a duplicate of BR-08 (supersession) and do not collapse the two.
- **Success measure "Trust" is the real acceptance test.** §7 says so outright: a store that exists but is not consulted has failed however complete it is. Measures above it are proxies.
- **§10 glossary is authoritative for BRD vocabulary only.** Root `AGENTS.md` carries the wider project glossary, and the two overlap on *Context*, *Label* and the three memory types. Where they disagree, the BRD wins for business intent and root wins for implementation naming.

## Migration Plans

Known incompleteness, identified against the braindump record on 2026-09-13 and deliberately not yet
written into the BRD. A future amendment should close these in order; do not assume the BRD is silent
on them by design:

- **Scope and citation rules are entirely absent** — the four dimensions (`product`, `customer`, `program`, `self`), their per-dimension citation rules, and enforcement at retrieval rather than at storage. The failure this prevents is a roadmap promise leaking into a specification as though it described shipped behaviour. Two knock-ons: a program/product mismatch is a *gap*, not a conflict, so BR-10's authority ranking does not cover it; and graduation into product canon is manual because no has-it-shipped signal exists.
- **"Same subject, new claim" has no requirement.** Subject-versus-claim is load-bearing upstream (dedup matches subject, versioning replaces claim) and nothing in §6 requires capture to recognise a known subject rather than accumulate restatements.
- **The automation gradient is unstated.** "Automate what is reversible; ask about what is not" generates BR-10, BR-13, BR-14 and the agentic-action exclusion. The BRD lists those consequences without the rule that produces them.
- **Four requirements have no validating evidence** — divergence, bitemporality, typed links, and provenance-with-confidence were never exercised by the fixtures used so far. §7 assumes they work.
- **BR-17 is stated as settled and is not.** Content-addressed bodies are not human-navigable, so readability depends on a reconstruction step across two stores; the BRD does not mention export at all. *Partly answered on 2026-09-13 by [BRD-002](../002-contextual-export/) `BR-21` for the curated case; the whole-store readability half remains unstated here.*
- **Losing the store is a stated risk — closed 2026-09-16 by BR-37.** On-demand verifiable copy and rebuild, recency visible; design answered by [HLD 006](../../hlds/006-corpus-snapshot-and-restore/) (In Discovery). BR-37 sits in BRD-001's Ownership section but is numbered after BRD-002's BR-18–36 because the two documents share one requirement space — do not renumber it to fit the section.

Closed on 2026-09-13 (second amendment): findability risk row in §9; BR-10 rewritten to state
authority ranking with genuine conflicts surfaced; BR-11 acceptance names the proposing actor and
moment (capture checkpoint); procedural preferences added to §5 in-scope; ticket references stated as
multi-tracker in BR-04.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-13 | Created alongside promoting the BRD from a flat file to `docs/brd/001-context-memory/`. Records the BRD/HLD authority direction, the three-place HLD reference rule, and eleven known gaps found against the braindump record. | — |
| 2026-09-13 | Second amendment applied to the BRD: BR-10 authority ranking, BR-11 checkpoint clause, findability risk, preferences in scope, multi-tracker tickets, terseness pass on §1/§2/§7. Five of eleven gaps closed; gap list pruned to the six remaining. | — |
| 2026-09-13 | BRD-002 recorded as extending this requirement space and owning HLD 005; three-place rule clarified for sibling-BRD-owned HLDs; BR-17 export gap marked partly answered. | BRD-002 |
| 2026-09-13 | HLD 004 added to the `Related` row and §11 — the three-place rule had been missed when it landed. | HLD 004 |
| 2026-09-16 | HLD 006 (corpus snapshot and restore, In Discovery) added to the `Related` row and §11; the "losing the store is not a risk" gap marked as design-side answered, BRD-side still unstated. | HLD 006 |
| 2026-09-16 | Third amendment: BR-37 appended (store survives loss of its machine — on-demand verifiable copy, rebuild, recency visible) with a matching §9 risk row; durability gap closed. Numbered BR-37 to respect BRD-002's BR-18–36 continuation. | BR-37; HLD 006 |
