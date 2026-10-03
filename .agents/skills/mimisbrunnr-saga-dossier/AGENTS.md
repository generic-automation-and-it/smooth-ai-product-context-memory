# mimisbrunnr-saga-dossier — AGENTS.md

## TL;DR

Read-only dossier composer. Requests a **bundle** from the HLD-005 bundle/preview API, composes the
ordered, consolidated, cited **dossier** document and its **findings**, applies an optional **focus**,
writes a local gitignored artefact, and writes nothing back to the store. Composition is judgement
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

The `bundle` request is the skill's only network call. It seeds the read token and base URL from the
machine credential file at import (read token + base URL only, never a write token — read-only
LADR-08/NFR-06), builds the anchor body in the contract's field names (the endpoint rejects unknown
properties), and always sends `widenDepth`. `--ticket`/`--tickets` map to one `ticketProvider` +
`ticketKey` pair; more than one ticket is refused, never truncated. Heimdallr autofill uses the same
builder and fills tickets from the branch only (commit-subject tickets are PR numbers).

### Ordering (LADR-07)

Topological sort over `supersedes` / `depends_on` / `implements` only. `relates_to` and unknown relations
connect **without** ordering — including them manufactures cycles. Tiebreak: business-time validity, then
capture time, then memory identity. A provenance cycle is a **finding** (`provenance-cycle`), not a
rendering problem: break at a stated point, report it, and still produce the document.

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
  exclusions. **No evidence means no finding.** Reuse `mimisbrunnr-odin-context-memory/scripts/near_miss_tags.py`.
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
reader. The manifest's own count is the trustworthy left-hand side.

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
`judgements` to `compose(bundle, focus=..., judgements=...)`. The module has no store-write operation;
the only file it writes is the local dossier artefact. Tests: `tests/run_tests.py`.

## Changelog

> AI loading note: Skip this section during routine task execution. Use it only when updating this rule file.

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-03 | **Bundle request fixed from its documented flags and the machine credential.** `bundle` seeds `CONTEXT_MEMORY_READ_TOKEN`/`CONTEXT_MEMORY_BASE_URL` at import from `~/.mimisbrunnr/credentials` via the sibling loader (read token + base URL only — never the write token, read-only LADR-08/NFR-06; a write token present refuses to run; a missing read token is `missing-credential`, not a 403), builds the body in the contract's field names (`--repo`→`repo`, `--initiative`→`initiativeName`, `--ticket provider:key`→`ticketProvider`+`ticketKey`, `--tags`→`tags`, `widenDepth` always sent default 1 via `--widen-depth` 1-5), refuses `--tickets` with >1 value rather than truncating, autofills tickets from the **branch** only (commit-subject tickets are PR numbers), and surfaces the server's problem title/detail on HTTPError instead of discarding the body. Harness 60 -> 76 (`BundleBodyContractTests`, `BundleCredentialTests`, `BundleServerErrorTests`). | session request |
| 2026-10-03 | **Heimdallr scan skipped when anchors are fully bound.** `bundle` gates the reporter like kvasir `import` already did, so supplied `--repo`/`--tickets`/`--initiative` (or `--body` keys) mean no subprocess runs; per-field precedence restated in SKILL.md. Harness still 60. | session report |
| 2026-10-03 | **`bundle` gains anchor flags plus Heimdallr autofill (`--heimdallr true`, default on).** `--repo`/`--ticket`/`--tickets`/`--tags`/`--initiative` merge into `--body` (explicit keys win); missing repo/tickets fill from the sibling Heimdallr reporter via a skills-root-relative lookup (branch tickets else newest commit one, never tags). Autofill notes go to stderr so the stdout bundle JSON stays parseable. Harness 57 -> 60. | session request |
| 2026-10-02 | Renamed `mimisbrunnr-dossier` → `mimisbrunnr-saga-dossier` (folder, `name:`, CI paths, every cross-reference). Saga, goddess of history — she recounts what was. Behaviour unchanged; harness green. | session request |
| 2026-10-01 | The loopback guard refuses a base URL that `urlparse` cannot parse (an NFKC-confusable netloc character, a non-numeric port) with its fixed loopback message. The parser's own `ValueError` quoted the netloc — userinfo included — and `main` prints exception text, so `--base-url http://user:pass@local＃host` printed the password. `CredentialTransportTests` 5 -> 6, mutation-checked. | HLD-005 NFR-01 |
| 2026-10-01 | Findings section records that no `ghost` ("specified but not wired") category exists, per HLD-005 LADR-16, and that a missing `implements` edge is never a finding basis. Taxonomy unchanged. | HLD-005 LADR-16 |
| 2026-09-27 | `SKILL.md` gained the YAML frontmatter every other skill carries (`name`, one-line `description`, block-list `allowed-tools`, `effort` — the `smooth-devex-template` shape; switches stay documented in the body). Without it the skill listed with its bare name as description and no trigger text. No behavioural change. | skill frontmatter alignment |
| 2026-09-22 | Created — read-only dossier composer contract (LADR-02/04/05/07/08/12/13/14/15, NFR-01..07). | HLD-005; BRD-002 |
| 2026-09-23 | Implemented the judgement skeleton (`scripts/dossier_composer.py`), the invocation contract (`SKILL.md`), and the committed L0 harness (`tests/run_tests.py`, 34 tests, NFR-04/05/06/07). | HLD-005; BRD-002 |
| 2026-09-24 | Added the "Never consolidate on wording alone" Non-Negotiable (equivalence = meaning + applicability + lifecycle) and enforced it in the composer's equivalence-group validation. | HLD-005 LADR-05; PR #99 review |
| 2026-09-24 | Post-merge review follow-up: removed the dead `_origin_line` helper and `derive_findings`' unread parameters; the NFR-05 attribution test can no longer pass vacuously (surfaced claims must render statements, and at least one focus must examine some); README relabels `bundle` as the deterministic bundle, not the priced preview. | PR #99 review |
| 2026-09-26 | `README.md` rewritten for a human audience: what a dossier is and when to reach for it, a "what it is not" table, the bundle-vs-dossier trust distinction, a plain-language focus table, and the read-only/no-silent-drop guarantees restated without jargon. No behavioural or contract change — `SKILL.md`/`AGENTS.md` remain the authoritative contract. | README readability pass |
| 2026-09-26 | `README.md` quickstart now runs `mkdir -p .context/mimisbrunnr-saga-dossier` before step 1: `.context/` is gitignored, so on a clean checkout the shell redirection in step 1 and the `--out` write in step 2 both aborted on a missing parent directory. Documentation only — no script, contract or behavioural change. | PR review (Medium) |
| 2026-09-26 | README rewrite accuracy fixes: the focus bullet now states out-of-focus material is omitted with reason `outside-focus` rather than "just later" (the renderer does not reorder); the reconciliation bucket is `consolidated`, not `merged`, matching `SKILL.md`/`AGENTS.md`; and the sibling reference reads "the only path in this skill set that can write" rather than "the only thing in this system". Docs only. | PR #108 review |
| 2026-09-26 | The composer now refuses to send the read token off loopback and refuses redirects on credential-bearing requests, matching the guard the context-memory client already had: CPython's default redirect handler rebuilds the request with the original headers, so a 302 from a configured base forwarded `Authorization` to whatever host it named. Also builds the opener with proxies disabled so no proxy sees the header. | HLD-005 NFR-01 |
| 2026-09-27 | The two credential-transport guards are now covered by the committed harness (`CredentialTransportTests`, 5 tests): non-loopback origins refused including the suffix/prefix lookalikes (`localhost.evil.example`, `127.0.0.1.evil.example`) and non-HTTP schemes; the loopback forms accepted including uppercase and bracketed; a non-loopback base refused *before* any request is built; the opener composition pinned so dropping the redirect handler or re-enabling a proxy fails; and the handler raising rather than rewriting the request. Harness 34 -> 39. | PR review |
| 2026-09-27 | The composer's loopback guard now carries the sibling client's full origin condition (userinfo, path, query and fragment) instead of scheme + host alone, so `http://localhost:5141/api` is refused with an actionable message rather than posting to a doubled path and returning a 404. The base is normalised before the guard, so a trailing slash no longer doubles the separator. | PR #118 review |
| 2026-09-30 | **A history bundle is now composable, and the identity behind it is `(uuid, version)` rather than `uuid`.** The composer rejected any bundle carrying two versions of one memory — the duplicate-uuid guard read a history as a duplicate — and had it accepted them, `by_uuid` in `topological_order` and `claims_by_uuid` in `consolidate` would have collapsed a memory to whichever version came last while the reconciliation still closed, because the collapse happened upstream of anything that counts. Two rules keep the caller's contract uuid-scoped and unchanged: an **equivalence group names memories**, so it expands to every version of each named memory present in the slice and the applicability/lifecycle gate then runs across that whole set (so a memory whose v1 is superseded and v3 is current fails the gate and is reported `equivalence-uncertain` rather than merged as corroboration — LADR-04/05); and an **edge names a memory**, so `supersedes`/`depends_on`/`implements` order every version of it. A fourth, stated ordering constraint renders a memory's own revisions oldest-first, so the document does not depend on the business-key tiebreak happening to agree. **This required a wire-contract addition, not just a key swap:** `DossierOmittedItem` gained `Version`, because the cap and collapse cuts are decided per item and an omission naming only a memory could not say *which* version was cut — leaving the same memory legitimately on both sides of the reconciliation, indistinguishable from the double-count the composer refuses to render. The alternative on the table was to reject `includeHistory` at the API; that would have removed a shipped, tested capability to work around a client limitation, which is the wrong direction of travel. Harness 47 -> 56, and all four sites are mutation-checked: uuid-only duplicate guard, uuid-keyed topological order, uuid-keyed consolidation claims, and deletion of the intra-memory version constraint each fail the new class. The fourth was caught by the AI review gate, not by me — the first version of that test gave v1 and v3 strictly increasing business keys, so `_business_key` alone already ordered them [1, 3] and the test could not fail without the constraint. It is now back-dated, with its own precondition asserted, because a revision that corrects a record by asserting it was true all along carries the *earlier* `validFrom` and is the case the constraint exists for. | HLD-005 LADR-05, LADR-07, LADR-14
