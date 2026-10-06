# mimisbrunnr-saga-dossier — AGENTS.md

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
| 2026-10-06 | **Issue 186.** (1) A near-miss finding keeps the helper's evidence qualifications through composition and rendering — the observed tag mismatch, whether the supporting record is only proposed, who judged relevance, and the helper's standing qualification (rendered once per category) — instead of only the analysis sentence. (2) A Heimdallr timeout falls back to no autofill instead of a traceback. (3) A withheld repository (`repositoryWithheld`) is disclosed when the `repo` anchor would have been autofilled. (4) A server problem detail is bounded and single-line. Findings 5 and 6 (bundle and dossier content on disk) are the accepted HLD-005 NFR-01/06 design — every file the module writes is owner-only and gitignored, scratch is deleted at the end, and the bundle holds store content that was redacted at capture — so they are not changed. Mutation-checked. Harness 125 -> 128. | issue 186 |
| 2026-10-06 | **Fourth-round review fixes.** (1) **A repeated omission let an open reconciliation exit 0** (recurrence of issue 179). `validate_bundle` checked that the manifest counted every item and omission, but the reconciliation counts distinct `(uuid, version)` pairs, so a repeated omission passed, left the sum one short, rendered `✗ FAILED` and exited 0. `validate_bundle` now refuses a repeated omission, and `compose()` raises instead of returning a dossier whose reconciliation does not close, so the CLI exits 1 and writes nothing. Checked against HLD-005 first: NFR-04 accepts no open reconciliation, and BR-30's "marks incomplete output" is about reached limits, so no legitimate rendering is lost. The downstream patch only raised in `cmd_compose`; the raise is in `compose()` so a Python caller cannot get an open dossier either. (2) **The cycle finding listed memories that only depend on a cycle** (recurrence of issue 179). Every item Kahn's sort left unemitted was reported, and the leftovers were emitted in business-key order, so a memory depending on a cycle could render before it. The finding now lists the cyclic strongly connected components only, one finding per cycle. The break repeats at the earliest-business-key member of a cycle nothing else still waits on, and topological order resumes after each break. (3) **The per-focus test could not see a missing finding in a repeated category.** It compared sets of categories over a fixture with one finding. It now compares the full multiset of findings, and a new slice repeats every category it emits. (4) **The quickstarts called the judgements file optional but passed `--judgements` unconditionally**, which fails on a file never written. Both now say to drop that line. (5) **Personal data on disk** (recurrence): the scratch design stays as agreed (HLD-005 NFR-01/06, issue 182), since the bundle is already gitignored, 0600 and deleted. One real gap within it is now closed. NFR-01 requires the judgement inputs to be owner-only too, but the Write tool creates them 0644 and the documented `mkdir -p` made the scratch directory 0755. The workflow now uses `mkdir -p -m 700` (also in `allowed-tools`), and `compose` refuses a `--judgements` or `--near-miss-evidence` file when neither the file nor its directory is owner-only. Harness 112 -> 125, green on 3.9.6 and 3.13 and with both write tokens exported. Nine mutations each made the harness fail. The HEAD composer fails 7 of the new tests. | issue 184 |
| 2026-10-05 | **Heimdallr ticket screening disclosed.** `_heimdallr_autofill` read only `tickets`, so a branch ticket Heimdallr withheld as credential-shaped (`ticketsWithheld`), or a scan whose redactor could not load (`ticketsUnavailable`), looked like "no branch ticket". When a ticket autofill is attempted, either now prints one stderr line (`heimdallr: N ticket candidate(s) withheld as credential-shaped` / `heimdallr: tickets unavailable (<reason>)`) — count or reason only, never the withheld value. Mutation-checked: removing the print fails both subtests of the new `HeimdallrBundleAnchorTests` case. | issue 182 |
| 2026-10-05 | **Requirements-conformance follow-up to the issue-182 row below.** (1) **`--judgements` let an evidence-free `near-miss-tag` through** (`memories: []`), against LADR-10's "no evidence means no finding". `--judgements` now refuses the category outright, and the new `compose --near-miss-evidence PATH` is the only CLI route: it runs the shared `near_miss_tags.py` helper through `near_miss_findings()`. `compose()` itself also refuses a `near-miss-tag` naming no memory, for Python callers that skip the CLI. This closes the open item the row below left. (2) **Gap references follow their BR-27 ground.** An `included-claim` gap must name the claim it interprets. A `task` or `expectation` gap may name none: the answer is missing from the slice, so no selected memory supports it, and LADR-13 forbids citing one that does not. The findings dedup key now includes the basis, because with only category + memories every memoryless gap after the first was silently collapsed into it. (3) **Malformed nested judgements are a one-line refusal (exit 1), not a traceback.** Covers a string `memories`, non-object memory entries, an unhashable or non-string `uuid`, a boolean `version`, a non-object equivalence group and non-string `uuids`. References are shape-checked before the contradiction gate reads them. `compose` without `--bundle` is refused the same way. (4) `--judgements` and `--near-miss-evidence` must sit at a gitignored path: the agent writes them with the Write tool, which runs no ignore check, and both quote store content. (5) Doc drift: "writes only the local artefact" now reads "the dossier plus transient scratch files" in SKILL.md, this file, the module docstring and the NFR-06 test (renamed `test_cli_has_no_path_back_into_the_store`). HLD-005 NFR-06 and NFR-01 and BRD-002 §5 are amended to match (see their changelogs). Harness 104 -> 111, green on 3.9.6 and 3.13 and with both write tokens exported; ten mutations, each made the harness fail. Not changed: NFR-04's "every finding names the memories it concerns" is read as "names every memory it concerns" — a task/expectation gap concerns none. | issue 182 |
| 2026-10-05 | **Consumer-review fixes (issue 182).** (1) **The CLI dropped the agent's judgements.** `cmd_compose` called `compose()` without `judgements`, and the skill's allowed tools reach only the CLI, so a dossier could never carry a gap, a contradiction or a consolidation — SKILL.md documented a Python import the skill could not run. New `compose --judgements PATH` (JSON object, `equivalences`/`findings` only, unknown keys and non-list values refused; entries go through the existing gates). SKILL.md step 4 writes that file, and its example — previously a `gap` with no `ground` and versionless memories, which the gates refuse — is now a JSON block the harness composes. (2) The reconciliation fixture reported `anchors: 1, widened: 5, selected: 5` with every item reached via an anchor; a widening never returns its sources, so anchors + widened = selected. Now `5 / 0`, with a test checking every fixture's reach against its items. Not added to `validate_bundle`: under `includeHistory` the reach counts memories while items count versions, and a client-side rule the server is not pinned to would refuse real bundles. (3) **The bundle reached disk unguarded.** The documented `bundle > .context/…/bundle.json` redirect bypassed the `--out` ignore check and took the umask's mode, for a file holding every selected body. New `bundle --out` goes through `_require_ignored_destination` **before** the request and writes 0600 atomically (`mkstemp` + `os.replace`, so an existing 0644 file does not keep its mode); `compose --out` uses the same writer. The workflow saves into `.context/mimisbrunnr-saga-dossier/scratch/` and ends with `rm -rf` of that directory, also on failure or cancel; both commands are in `allowed-tools`. (4) `compose --bundle` refuses any URL: it was fetched through a default opener (any host, redirects followed, proxies honoured) beside a transport otherwise pinned to a guarded loopback origin, and no documented flow used it. (5) The base-URL guard refuses `;params` — `http://localhost:5141/;tok=x` has path `/` and passed. (6) The write-token refusal test now covers both spellings by literal name (dropping `ApiAccess__WriteToken` from `_WRITE_TOKEN_NAMES` used to leave the harness green), closing the follow-up the 2026-10-04 row left open. (7) The credential classes ran against the operator's shell: with `ApiAccess__WriteToken` exported (the provisioner writes it) five tests failed, and `CONTEXT_MEMORY_WRITE_TOKEN` passed only because an earlier test deleted it. A `_CleanCredentialEnv` mixin clears and restores every credential name per test, and a child-process test reruns those classes with both write tokens exported. Harness 94 -> 104, green on 3.9.6 and 3.13 and with both write tokens exported; every fix mutation-checked. Open: `near-miss-tag` evidence still goes through the `near_miss_findings()` Python helper, which the CLI does not expose. | issue 182 |
| 2026-10-05 | **Consumer-review fixes (issue 179).** (1) An explicit `--asof` that does not parse (`2026-13-45`) is refused in `compose()`; `_parse_time` returned `None` and the lifecycle/stale checks fell back to today while the dossier displayed the typo. (2) The contradiction gate now requires identical derived lifecycles, as `consolidate` does — two conflicting proposals are a contradiction (previously refused because any `proposed` member was); current-versus-superseded and current-versus-unknown are now refused alongside proposed-versus-shipped. (3) A rendered omission names `uuid vN` beside the name, so a history dossier that keeps v1 and cuts v3 says which was cut. (4) `compose --out` is refused unless `git check-ignore` reports the **resolved** destination ignored (a tracked file is never reported ignored, and resolving defeats a symlink to one); outside a work tree it is refused, stdout remains. (5) New `preview` subcommand (same anchor flags, `POST /api/context/dossier/preview`, shared read-only transport) so the LADR-14 consent step is runnable with the skill's own allowed tool; README quickstart and SKILL.md workflow now preview → approve → save the **whole** bundle to `.context/mimisbrunnr-saga-dossier/bundle.json` (SKILL.md piped it to `head` and composed a `bundle.json` never written) → compose. (6) Tests: the non-loopback refusal test now supplies a fake read token and a recording opener and asserts nothing is built or opened (a missing-credential error previously satisfied it); the store-wide-language test gained a finding-bearing case (it iterated an empty list). Harness 76 -> 94; each behaviour fix mutation-checked; green on 3.9.6 and 3.13. HLD-005 references in Contexts left as-is — `docs/hlds/005-contextual-export/` exists in this repo. | issue 179 |
| 2026-10-04 | **The bundle's write-token refusal checked one spelling of a credential that has two.** `_resolve_read_credentials` refused only `CONTEXT_MEMORY_WRITE_TOKEN`, while the established read path treats the two forms as one credential — the kvasir client strips `CONTEXT_MEMORY_WRITE_TOKEN` **and** `ApiAccess__WriteToken` from a read subprocess precisely so a read surface stays read-only "whichever form the shell set", and the provisioner writes the Host's `ApiAccess__WriteToken` beside the skill name. An operator who had sourced the provisioned env file therefore held a live write credential in this read-only composer's environment while the refusal stayed silent — contradicting this file's own `No write capability` non-negotiable and the function's own docstring ("a write token present is refused before anything else"). Both spellings are now refused, via `_WRITE_TOKEN_NAMES`; the message names **whichever form is actually present**, so the refusal stays actionable instead of naming a variable the operator never set. The composer never reads the Host form, so no request changes — this closes the boundary, not a live capability. **Unpinned by the harness:** `BundleCredentialTests.test_a_write_token_present_refuses_the_bundle_request` covers the skill spelling only, so the second branch has no committed test; one asserting `ApiAccess__WriteToken` also refuses (and that the message names it) is the obvious follow-up. Test edits were out of scope for this pass. | PR review |
| 2026-10-04 | Review clean-up: removed the dead `--base-url`→`CONTEXT_MEMORY_BASE_URL` env write in `cmd_bundle` — `fetch_bundle_from_api` passes the flag straight to `_resolve_read_credentials`, so the env write no longer feeds anything. | code review |
| 2026-10-04 | Review hardening: `_problem_summary` never raises past `main()` (a truncated HTTP error body can raise `http.client.IncompleteRead`, which an OSError-only handler missed), and the half-specified `--body` ticket refusal names the fix (drop the partial fields, use `--ticket` alone). | code review |
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
