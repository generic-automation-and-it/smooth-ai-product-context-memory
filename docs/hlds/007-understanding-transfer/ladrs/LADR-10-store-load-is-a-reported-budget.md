# LADR-10: A store load is a reported budget of whole records

**Status:** Accepted

## Context

A load's breadth is controlled (LADR-08), but its **size** is not: a store export renders every selected
record, so a real `--all` corpus is handed to the agent in full and the agent pays for every character.
The walk harness measures a *representative* load at ~1.7–2.0k characters, but nothing bounds the
pathological one.

The obvious fix — truncate the rendered text at N characters — is forbidden here. The dossier rule and
HLD-007's own stance are that a condition is **never compressed away to fit a budget**: a record cut
mid-sentence is a corrupted fact, and an agent cannot tell a truncated claim from a whole one.

## Decision

One `--max-chars` budget (default `DEFAULT_MAX_CHARS`, 12000) applies to **both** render surfaces — a
store export and foreign material. On a store export:

- the budget bounds the **rendered records**, not the header or the closing notice;
- records render in the **source's own order** (a store export's array order, or the newest-version-per-slug
  order a folder resolves to);
- the first record that would exceed the budget **ends the render**: it and every later record are cut,
  **never partially rendered**;
- every cut record is listed by **identity** (`uuid vN`, else origin, else subject) with the budget as
  the reason, beside the cap, the rendered size and the count cut — in the same header that already
  states breadth.

`max_chars=None` is the pre-cap render and is **byte-identical** to an under-budget render: the cap adds
no line and cuts nothing for material that fits.

## Alternatives Considered

- **Truncate the rendered text at N characters** — rejected: compresses a record, which the no-compression
  rule forbids and which makes a truncated claim indistinguishable from a whole one.
- **A separate store-load cap number** — rejected: two numbers drift, and the walk harness justifies one
  budget for both surfaces. One number, one place to change it.
- **Rank records (by date or relevance) before cutting** — rejected here: ranking is a judgement the
  requirement does not ask for and a model is out of scope for this client; the requirement is a
  *deterministic* order. A ranking is a separate decision if it is ever wanted.
- **Skip-and-continue** (drop an oversized record, keep a later smaller one) — rejected in favour of the
  tail-cut: tail-cut is predictable and statable ("everything after the budget line is cut") and matches
  the `safeCutoff` shape, where whole ranked candidates are taken until the budget and then it stops.

## Consequences

- A load is bounded, and its shortfall is **reported** rather than silent: the cap, the rendered size, the
  count cut and each cut record's identity are all in the output.
- The under-budget path is unchanged byte-for-byte, so the cap never touches a representative load. The
  walk harness re-run at the default is identical (1682 / 2006 / 2238 chars) — the cap bounds the
  pathological corpus, not the normal one.
- A budget below the smallest record narrows to **zero** records rather than truncating one. That is the
  honest extreme of "narrow and report"; the default never reaches it.
- The dossier is **out of scope**: its omission reasons are a bounded set fixed before first use, and
  adding a budget reason there is a separate decision.

## Related

- **LADR-08** — breadth is a filter; the cap applies **after** breadth selection.
- **NFR-01** — a load writes nothing, capped or not.
- **BRD-003 BR-42** — controlled breadth; this bounds size as breadth bounds kind.

## Evidence (2026-10-01)

Skill L0 `.agents/skills/mimisbrunnr-understanding/tests/run_tests.py`: an under-budget render is
byte-identical to `max_chars=None` and emits no `Budget:` line; an over-budget render cuts whole records,
names each by identity with the budget as the reason, never partially renders the oversized record, and
is identical across runs; a budget below the smallest record narrows to zero. Walk harness re-run at the
default: sizes unchanged.
