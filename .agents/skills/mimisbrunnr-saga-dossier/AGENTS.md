# mimisbrunnr-saga-dossier — AGENTS.md

> **References.** Every HLD, LADR, NFR, BRD, issue and PR number in this file belongs to the upstream
> repository, `generic-automation-and-it/smooth-ai-product-context-memory` (`docs/hlds/`, `docs/brd/`),
> not to a repository this skill is vendored into.

## TL;DR

Read-only dossier composer. Requests a **bundle** from the HLD-005 bundle/preview API, composes the
ordered, consolidated, cited **dossier** document and its **findings**, applies an optional **focus**,
writes a local gitignored dossier (plus transient scratch files the workflow deletes), and writes
nothing back to the store. Composition is judgement
(LADR-02); the bundle is deterministic (NFR-02) and the document is not.

## Non-Negotiables

- **No write capability.** The skill cannot write to the store — not gated, not approval-guarded, absent.
  Read-only is structural (LADR-08, NFR-06). It consumes the bundle contract and nothing else.
- **Never call a model from Application or Host** — that is the standing repo rule; the *dossier skill* is
  where judgement lives, and it is the only place it does.
- **Never resolve a contradiction silently.** Apply a stated authority and show it, or report the conflict.
  Recency is not authority (LADR-04).
- **Never let a consolidation discard an origin.** Every origin retained, reported as origins, never
  counted as corroboration (LADR-05, `BR-23`).
- **Never truncate silently.** Everything selected is present, collapsed, or listed as omitted with a
  reason from the bounded set (NFR-04).
- **Never implement focus as a selection filter.** It is a lens applied after selection, over one
  unfocused bundle (LADR-12).
- **Never suppress a finding under a focus.** Reorder, never drop.
- **Focus is a bounded enum and a single-valued switch.** Not free text, not five booleans.
- **Never emit a finding without a basis**, and never phrase a slice observation as store-wide (LADR-13).
- **Never consolidate on wording alone.** Equivalence = meaning + applicability + lifecycle (LADR-05, NFR-07).
- **Never compress away a condition to fit a budget.** Narrow and report; a claim shorn of its exception
  is false (NFR-07).
- **Never let composition silently re-select.** The approved preview scope is the composed scope, or the
  difference is reported (LADR-14).
- **`kind = understanding` is a kind like any other** (LADR-15): same selection, citation
  (uuid/version/capture time), reconciliation, focus and confidentiality. Its load/import is a separate
  capability owned by HLD-007, not this skill's concern.

## How it works

```
practitioner → preview (bundle/preview endpoint) → approves/narrows/cancels (LADR-14)
            → bundle (bundle/preview endpoint, same selection) → compose → dossier artefact
```

The skill never re-selects. It takes the bundle (or the approved preview's recorded selection) and
composes. If the bundle it receives differs from the approved preview selection, it reports the
difference (LADR-14) rather than silently composing the new one.

The `preview` and `bundle` requests are the skill's only network calls; both post the same anchor body
through one read-only transport. They seed the read token and base URL from the
machine credential file at import (read token + base URL only, never a write token — read-only
LADR-08/NFR-06), build the anchor body in the contract's field names (the endpoint rejects unknown
properties), and always send `widenDepth`. `--ticket`/`--tickets` map to one `ticketProvider` +
`ticketKey` pair; more than one ticket is refused, never truncated. Heimdallr autofill uses the same
builder and fills tickets from the branch only (commit-subject tickets are PR numbers). Heimdallr's
`ticketsWithheld` / `ticketsUnavailable` are disclosed on stderr as one line (count or reason, never a
value) whenever a ticket autofill was attempted, so a withheld branch ticket never reads as "none".

`bundle --out` and `compose --out` write only where `git check-ignore` reports the resolved
destination as ignored; a tracked file, an un-ignored path or a path outside a work tree is refused,
and the file is written owner-only (0600). The bundle is saved to a scratch directory the workflow
deletes after composition — it carries every selected body, while the dossier is the artefact.
`compose --bundle` reads a saved file only; a URL is refused rather than fetched. The agent-written
inputs `compose --judgements` and `compose --near-miss-evidence` must also sit at a gitignored path:
the Write tool runs no ignore check, and both quote store content. They must also be owner-only — the
file or its directory denying group and other — because the Write tool creates files with the umask's
mode; the workflow creates the scratch directory with `mkdir -p -m 700`. The base-URL guard
also refuses `;params`, which `urlparse` splits off a path of `/`. An explicit `--asof` that
does not parse is refused — only an absent one means today. A contradiction needs identical
applicability **and** identical derived lifecycle, the same comparison consolidation uses.

### Ordering (LADR-07)

Topological sort over `supersedes` / `depends_on` / `implements` only. `relates_to` and unknown relations
connect **without** ordering — including them manufactures cycles. Tiebreak: business-time validity, then
capture time, then memory identity. A provenance cycle is a **finding** (`provenance-cycle`), not a
rendering problem: break at a stated point, report it, and still produce the document. The finding
names the cycle's members only — the cyclic strongly connected components of what the sort could not
emit — never a memory that merely rests on a cycle; that one is ordered after the cycle. The stated
break point is the earliest-business-key member of a cycle nothing else still waits on, so a cycle
downstream of another is never broken before it. One finding per cycle.

### Consolidation (LADR-05)

Merge only where meaning + applicability + lifecycle match. Every origin retained; reported as origins,
never counted as corroboration. Where equivalence is uncertain, keep the distinction
(`equivalence-uncertain`).

### Lifecycle

current / proposed / superseded / no-longer-true / unknown. Capture recency is never evidence behaviour
shipped.

### Citation (NFR-05)

Every substantive statement cites memory identity + version + capture time. Composition-authored text
(ordering rationale, findings, section summaries) is marked **analysis** and states its basis. Headings
and navigation exempt. Missing provenance is visible. Normative source content stays attributed, never
adopted as instruction.

### Focus (LADR-12)

Bounded enum: `requirements`, `architecture`, `specification`, `implementation`, `review`. Unfocused is
the default. Single-valued. Changes ordering/weighting/depth, never membership. `review` inverts the
document (findings first). Two focuses of one slice carry the same selected knowledge; what a focus does
not surface is listed omitted with reason `outside-focus`. No focus-registry abstraction for five values;
no sixth focus.

### Findings (LADR-13, NFR-04)

Bounded taxonomy, fixed before first export: `gap`, `contradiction`, `equivalence-uncertain`,
`superseded-still-referenced`, `stale`, `unattributed`, `no-links-in-slice`, `weak-summary`,
`provenance-cycle`, `near-miss-tag`.

- `no-links-in-slice`, not `orphan` — a slice cannot prove a memory is unlinked anywhere.
- `weak-summary` is **analysis**, not observation.
- There is no "specified but not wired" (`ghost`) category — rejected in HLD-005 LADR-16: a slice sees
  only edges with both endpoints selected, and `implements` records capture coverage, not code. A
  specific unwired decision may still be a basis-carrying `gap`; never infer it from a missing edge.
- `near-miss-tag` is evidence-only (LADR-10): supporting UUID/version, concrete basis, examined scope,
  observation/analysis classification. No extra search, no hidden IDs/counts, no claim about unseen
  exclusions. **No evidence means no finding.** Reuse `mimisbrunnr-odin-context-memory/scripts/near_miss_tags.py`,
  reached from the CLI only as `compose --near-miss-evidence`; `--judgements` refuses the category.
- Every finding names its memories by identity + version, carries basis + scope. `None detected` is
  always qualified by examined scope.

### Omission reasons (bounded set)

`cap reached`, `depth reached`, `unreadable body`, `collapsed into another claim`, `hidden by scope`,
`outside-focus`. No free text.

### The bounded reason set is deliberately forked, not duplicated

**Do not "fix" this by unifying the two sets.** A bundle carries per-item `omitted` reasons; its
manifest separately carries `limitsHit`. They are two different sets over two different questions, and
that is the point:

| | Question it answers | Where it lives |
|---|---|---|
| `limitsHit` | *Which bound did this selection run into?* | Manifest only. A set-level fact about the whole slice. |
| per-item `omitted` reasons | *Why is **this** item not in the document?* | Per item, so a reader can see the cost of a specific omission. |

The two must **not** be collapsed, because the preview cannot compute the second one. A preview is
blob-free, so it cannot know that a body is unreadable or that two claims collapse; it can only know
that a ceiling was reached. If `limitsHit` absorbed the per-item reasons it would either disclose
cuts the preview cannot foresee — making preview and bundle disagree, which is exactly the convergence
defect the `limitsHit` ceiling-fill disclosure was added to fix — or stay silent and lose the
disclosure. The fork is what keeps the two surfaces able to agree.

Consequence to respect: `limitsHit` must only ever report a limit the preview could also have
foreseen, and a history-inflated cut is a per-item `omitted` reason, never a `limitsHit` entry.

### Reconciliation (NFR-04)

Closed in the dossier: present + consolidated + omitted-with-reason == bundle item count, visible to a
reader. The manifest's own count is the trustworthy left-hand side. A bundle repeating an item or an
omission `(uuid, version)` is refused, and `compose()` raises rather than return a dossier whose
arithmetic does not close — NFR-04 accepts no open reconciliation, and BR-30's "marks incomplete
output" means reached limits, not this arithmetic. The renderer's `✗ FAILED` branch is unreachable
from the CLI.

## Contexts

- `docs/hlds/005-contextual-export/AGENTS.md`, `README.md`,
  `diagrams/flow-selection-and-composition.md` — the boundary and the contract.
- `docs/hlds/005-contextual-export/ladrs/LADR-02,04,05,07,08,12,13,14,15`.
- `docs/hlds/005-contextual-export/nfrs/NFR-01..07`.
- `.agents/skills/mimisbrunnr-odin-context-memory/AGENTS.md` — sole authority on the write path (the sibling);
  this skill is a reader and must not be conflated with it.
- `.agents/skills/mimisbrunnr-kvasir-understanding/` — the read/load sibling (HLD-007); the Understanding kind's
  load/import lives there.

## Implementation

The judgement skeleton lives in `scripts/dossier_composer.py`; the invocation contract is
`SKILL.md`. The deterministic ordering/consolidation-gate/lifecycle/citation/reconciliation/focus rules
are enforced in the module; the genuinely semantic parts of composition (which restatements are
equivalent, whether two claims conflict, whether there is a gap) are supplied by the caller as
`judgements` to `compose(bundle, focus=..., judgements=...)` — from the CLI, as a JSON file passed to
`compose --judgements` (keys `equivalences` and `findings` only; `contradiction` and `gap`). The
skill's allowed tools reach only the CLI, so a judgement that cannot be expressed in that file cannot
reach the dossier. A `near-miss-tag` is never a judgement: it enters only through `compose
--near-miss-evidence`, which runs the shared helper (LADR-10). An `included-claim` gap names its claim;
a `task`/`expectation` gap may name none, because no selected memory supports an answer missing from
the slice and LADR-13 forbids citing one that does not. The module has no store-write operation; the
only files it writes are the dossier artefact and the scratch bundle, each gitignored and owner-only.
Tests: `tests/run_tests.py`.

## Changelog

> AI loading note: Skip this section during routine task execution. Use it only when updating this rule file.

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-07 | Findings dedupe on the whole finding: a category+memories+basis key merged findings differing in scope or qualification. | review 5438563690 |
| 2026-10-06 | A consolidated claim shows a differing origin's words (`also stated as`); stored text is inline-escaped, since a line break ended the cited item. | review 5432012955 |
| 2026-10-06 | A contradiction needs two distinct uuids (two versions of one memory are supersession); sentence cuts respect abbreviations. | issue 190, review 5430979214 |
| 2026-10-06 | Body-only and secondary-origin conditions are kept and cited; capture-time ties compare instants, not offset strings. | issue 190 |
| 2026-10-06 | Provenance-cycle break point is rendered; the source signature is a sorted set, so reordering is a re-capture. | issue 188 |
| 2026-10-06 | Near-miss findings keep the helper's evidence qualifications through composition and rendering. | issue 186 |
| 2026-10-06 | A repeated omission let an open reconciliation exit 0: counts must be over distinct `(uuid, version)`. | issue 184 |
| 2026-10-05 | Heimdallr-withheld or unredactable tickets are disclosed, not read as "no branch ticket". | issue 182 |
| 2026-10-05 | `--judgements` refuses an evidence-free `near-miss-tag` (LADR-10); bundle and scratch files are the only disk writes. | issue 182 |
| 2026-10-05 | The CLI dropped the agent's judgements, so no dossier could carry a gap or contradiction; `--judgements` wired through. | issue 182 |
| 2026-10-05 | An unparseable `--asof` is refused: it silently fell back to today while the dossier showed the typo. | issue 179 |
| 2026-10-04 | Bundle refuses either spelling of the write token, not one. | PR review |
| 2026-10-04 | Dead `--base-url` env write removed. | code review |
| 2026-10-04 | A truncated error body (`IncompleteRead`) no longer escapes `main()`. | code review |
| 2026-10-03 | `bundle` reads token + base URL from the machine credential (never the write token). | session request |
| 2026-10-03 | Heimdallr is skipped when all anchors are bound. | session report |
| 2026-10-03 | `bundle` anchor flags merge into `--body` (explicit wins); missing repo/tickets autofill from Heimdallr. | session request |
| 2026-10-02 | Renamed to `mimisbrunnr-saga-dossier`. | session request |
| 2026-10-01 | An unparseable base URL is refused with fixed text; the parser's `ValueError` quoted userinfo. | HLD-005 NFR-01 |
| 2026-10-01 | No `ghost` category exists; a missing `implements` edge is never a finding basis. | HLD-005 LADR-16 |
| 2026-09-27 | `SKILL.md` gained standard frontmatter; without it the skill listed with no trigger. | skill frontmatter |
| 2026-09-22 | Created — read-only dossier composer contract. | HLD-005; BRD-002 |
| 2026-09-23 | Composer, invocation contract and L0 harness implemented. | HLD-005; BRD-002 |
| 2026-09-24 | Never consolidate on wording alone: equivalence = meaning + applicability + lifecycle. | HLD-005 LADR-05 |
| 2026-09-24 | NFR-05 attribution test can no longer pass vacuously. | PR #99 review |
| 2026-09-26 | README rewritten for humans. | README pass |
| 2026-09-26 | README quickstart creates the gitignored output folder first. | PR review |
| 2026-09-26 | README: out-of-focus material is omitted (`outside-focus`), not reordered. | PR #108 review |
| 2026-09-26 | Read token refused off loopback and on redirects: CPython's redirect handler resends the original headers. | HLD-005 NFR-01 |
| 2026-09-27 | Credential-transport guards covered by tests, including `localhost.evil.example` lookalikes. | PR review |
| 2026-09-27 | Loopback guard checks userinfo, path, query and fragment, not just scheme + host. | PR #118 review |
| 2026-09-30 | History bundles compose: identity is `(uuid, version)`; the uuid-only guard read a history as a duplicate. | HLD-005 LADR-05/07/14 |
