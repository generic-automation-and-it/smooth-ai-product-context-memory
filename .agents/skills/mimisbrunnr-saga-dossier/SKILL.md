---
name: mimisbrunnr-saga-dossier
description: Compose a read-only, cited context dossier — one ordered document plus a findings report (gaps, contradictions, stale claims) — for a slice of the Mímisbrunnr store (repository, initiative, ticket, tags). Use when re-entering a repo or ticket, handing reasoning to a colleague, grounding a design document, or auditing the store. Fetches a deterministic bundle from the Host API and writes only the gitignored dossier plus transient scratch files it deletes; never writes to the store. Triggers on "dossier", "catch me up on", "everything the store knows about".
allowed-tools:
  - Bash(python3 -B .agents/skills/mimisbrunnr-saga-dossier/scripts/dossier_composer.py:*)
  - Bash(mkdir -p -m 700 .context/mimisbrunnr-saga-dossier/scratch)
  - Bash(rm -rf .context/mimisbrunnr-saga-dossier/scratch)
  - Read
  - Write
effort: high  # equivalence, contradiction and gap judgement across a whole store slice
---

# mimisbrunnr-saga-dossier

Compose a **context dossier** — a read-only, focused, cited document plus its findings — for a slice of
the Mímisbrunnr store. **This skill is read-only (LADR-08 / NFR-06): it has no write capability at
all.** It requests a deterministic **bundle** from the Host API, applies judgement to compose the
**dossier**, and writes only the dossier artefact to a gitignored path, plus transient scratch files
(the bundle and your judgements) under `.context/mimisbrunnr-saga-dossier/scratch/` that the workflow
deletes at the end. It never writes to the store and never calls a write endpoint.

Requires **Python 3.9 or newer**. The composer normalises a sub-second fraction before parsing,
because `datetime.fromisoformat` only accepts an arbitrary number of fractional digits from 3.11
while `System.Text.Json` emits a 7-digit tick count or a trailing-zero-trimmed fraction. Without
that, every capture timestamp silently became "unknown" on 3.9 and 3.10 — no error, just a dossier
that looked complete and carried no capture times. The npm launcher (`npm/cli/_run.js`) checks the
floor and refuses below it.

Use this when a practitioner wants *everything the store knows about one slice of work, in one readable
artefact* — re-entering a repository, handing reasoning to a colleague, writing a design document, or
assessing the store's gaps and contradictions.

## The two halves — bundle and dossier

| | Who | Deterministic? |
|---|---|---|
| **Bundle** | The Host API (`POST /api/context/dossier/bundle`) | Yes — byte-identical for an unchanged store (NFR-02) |
| **Dossier** | **This skill** (judgement) | No — it is a projection, not a verdict |

The skill *never re-selects*. It takes the bundle and composes. If the composed bundle differs from the
selection the practitioner approved in the preview, the difference is reported (LADR-14), never absorbed.

## Workflow

```bash
# 0. Create the scratch directory owner-only. The Write tool creates files with the umask's mode
#    (0644 under the usual 022), so this directory is what keeps the judgement inputs private;
#    compose refuses an input that neither it nor its directory keeps owner-only. A scratch directory
#    left from an earlier run is not re-permissioned by -m: remove it first (step 6).
mkdir -p -m 700 .context/mimisbrunnr-saga-dossier/scratch

# 1. Preview the selection (NFR-03 / LADR-14) — POST /api/context/dossier/preview, read-only and
#    blob-free. Takes the same anchor flags as `bundle` and returns the effective selection, volume,
#    reach, cost estimate and limitsHit.
#    Anchor flags (--repo/--ticket/--tickets/--tags/--initiative/--widen-depth) merge into --body,
#    built in the contract's field names (the endpoint rejects unknown properties); only a
#    missing repo/ticket anchor autofills from the offline Heimdallr git scan (--heimdallr true,
#    default on; a supplied flag or --body key is never overwritten for that field; tags are never
#    autofilled; a credential-shaped branch ticket Heimdallr withheld, or a scan whose redactor
#    could not load, prints one stderr line with the count or reason, never the value). The contract takes ONE ticket, so --ticket provider:key maps to ticketProvider +
#    ticketKey, and --tickets with more than one value is refused rather than truncated. widenDepth
#    is always sent (default 1; --widen-depth sets it, 1-5).
python3 -B .agents/skills/mimisbrunnr-saga-dossier/scripts/dossier_composer.py \
  preview --repo owner/repo --ticket github:160
# (the script path above is relative to the skills root, so it holds under
# .agents/skills, .claude/skills or .codex/skills; --heimdallr false disables autofill)

# 2. STOP. Show the practitioner the preview and get an explicit approve / narrow / cancel. Narrowing
#    means new flags and a new preview. Never fetch the bundle on an unapproved scope.

# 3. Fetch the deterministic bundle with the approved anchors into the scratch directory. --out goes
#    through the same gitignored-destination check as compose --out and is written owner-only
#    (0600). The bundle is store content only: the capture rule keeps personal data out of the store
#    (mimisbrunnr-odin-context-memory removes it before any file is written), so the bundle, like the
#    dossier composed from it, holds none. Never use a shell redirect instead. Compare its manifest.selection with the approved preview's
#    selection; report any difference (LADR-14) and re-preview rather than composing it.
python3 -B .agents/skills/mimisbrunnr-saga-dossier/scripts/dossier_composer.py \
  bundle --repo owner/repo --ticket github:160 \
  --out .context/mimisbrunnr-saga-dossier/scratch/bundle.json

# 4. Read the bundle and write your semantic judgements (see "Invoking the judgement") with the
#    Write tool to .context/mimisbrunnr-saga-dossier/scratch/judgements.json. Refer to memories by
#    uuid and version and to people by role: a judgement adds no personal data to a file — any
#    personal identifier (GDPR personal data) is masked and generalised, as the capture rule requires. The file is optional:
#    without it the dossier carries no gap, contradiction or consolidation — only the deterministic
#    findings — and step 5 runs WITHOUT its --judgements line (passing a path you never wrote fails).
#    A near-miss-tag is not a judgement: write its evidence to scratch/near-miss.json and pass it
#    with --near-miss-evidence (see Rules). Both paths must be gitignored and owner-only (the 0700
#    scratch directory from step 0 does that) or compose refuses them.

# 5. Compose from the saved bundle and judgements, apply an optional focus, write the artefact.
#    Drop the --judgements line if you skipped step 4.
#    --out must be a gitignored path inside the checkout (verified with `git check-ignore`; a
#    tracked, un-ignored or out-of-checkout destination is refused) and is written 0600; omit it to
#    print to stdout. Re-run with other focuses against the same scratch files as needed. A
#    reconciliation that does not close is a composer defect: compose exits 1 and writes nothing.
python3 -B .agents/skills/mimisbrunnr-saga-dossier/scripts/dossier_composer.py \
  compose --bundle .context/mimisbrunnr-saga-dossier/scratch/bundle.json \
  --judgements .context/mimisbrunnr-saga-dossier/scratch/judgements.json \
  --focus architecture --out .context/mimisbrunnr-saga-dossier/architecture.md

# 6. Delete the scratch directory once composition is finished — also when a step failed or the
#    practitioner cancelled. The bundle is raw store content; only the dossier is the artefact. If a
#    bundle does carry personal data, the store holds it from before the capture rule: the fix is at
#    the store, through mimisbrunnr-odin-context-memory, never by editing the dossier.
rm -rf .context/mimisbrunnr-saga-dossier/scratch
```

`--asof YYYY-MM-DD` (or an ISO-8601 timestamp) bounds the validity window used for lifecycle and
staleness; omitted means today, and a supplied value that does not parse is refused.

`--focus` is a **single-valued bounded enum** — `requirements`, `architecture`, `specification`,
`implementation`, `review` — or omitted for the unfocused default (LADR-12). An unknown focus fails. A
focus is a lens over one unfocused bundle: it changes ordering, weighting and depth, never membership,
and what it does not surface is listed omitted with reason `outside-focus`. `review` inverts the
document (findings first).

## Invoking the judgement

Composition is judgement (LADR-02). The deterministic skeleton in
`scripts/dossier_composer.py` enforces the rules and delivers the invariants; the **semantic** parts —
which restatements are truly equivalent, whether two claims truly conflict, whether there is a gap —
are the agent's to supply as a judgements file passed to `compose --judgements` (the CLI hands it to
`compose(bundle, judgements=...)` unchanged). Every memory reference names a uuid **and** a version
from the bundle; replace the placeholders with real identities:

```json
{
  "equivalences": [
    {"uuids": ["<uuid-a>", "<uuid-b>"], "meaning": "same default rule"}
  ],
  "findings": [
    {"category": "gap", "ground": "task", "basis": "No rollout plan is captured for this rule",
     "memories": [{"uuid": "<uuid-a>", "version": 1}], "classification": "analysis"}
  ]
}
```

- `equivalences` — consolidation candidates (LADR-05); a group names memories, not versions.
- `findings` — only `contradiction` and `gap` are accepted here (the composer derives the deterministic
  categories itself, and a `near-miss-tag` comes only from `--near-miss-evidence`). A `gap` needs a
  `ground` of `task`, `included-claim` or `expectation` (BR-27). An `included-claim` gap names the
  claim it interprets; a `task` or `expectation` gap is an answer missing from the slice and may name
  no memory — never cite one that does not support it (LADR-13). A `contradiction` names at least two
  memories. Each needs a non-empty `basis` and a `classification` of `observation` or `analysis`.
- Only the keys `equivalences` and `findings` are accepted. Anything else, a malformed entry, or a
  finding that fails its gate refuses the whole compose rather than dropping it; an equivalence that
  fails the applicability/lifecycle gate is kept distinct and reported `equivalence-uncertain`.

The composer then enforces the deterministic gates: applicability + lifecycle must match before a
consolidation, and a contradiction requires identical applicability and identical derived lifecycle —
so a scoped exception or a proposed-versus-shipped pair is never a contradiction, while two conflicting
proposals are (LADR-04/05). Every finding is emitted with a basis, a scope (the examined material, never the store),
and its memories by identity + version (LADR-13). Findings are focus-invariant in presence.

## What the composer guarantees

- **Ordering** — topological over `supersedes` / `depends_on` / `implements` only; a provenance cycle
  is reported (its members only — a memory that merely rests on a cycle is ordered after it, not
  reported as part of it) and still produces the document (LADR-07).
- **Consolidation** — merges only where meaning + applicability + lifecycle match; every origin
  retained and reported as origins, never counted as corroboration (LADR-05).
- **Lifecycle** — current / proposed / superseded / no-longer-true / unknown, with capture recency
  never treated as evidence behaviour shipped (NFR-07).
- **Citation** — every substantive statement carries memory identity + version + capture time;
  composition-authored text is marked **analysis** and states its basis; missing provenance is visible
  (NFR-05).
- **Reconciliation** — present + consolidated + omitted-with-reason == the manifest's selected count,
  closed in the dossier itself (NFR-04). A bundle repeating an item or an omission is refused, and a
  reconciliation that would not close produces no dossier (exit 1) rather than a "✗ FAILED" one.
- **Read-only** — the module exposes no write operation; the only files it writes are the dossier
  and the scratch bundle, each at a requested gitignored path and owner-only (0600) (NFR-06). A
  `--bundle` URL is refused: a bundle is read from a saved file only.

## Rules

- **Never write to the store, and never call a write endpoint.** Read-only is structural, not a
  policy you can waive.
- **Never call a model from Application or Host** — judgement lives in this skill and nowhere else.
- **Never resolve a contradiction silently.** Apply a stated authority and show it, or report the
  conflict. Recency is not authority.
- **Never truncate silently.** Everything selected is present, collapsed, or omitted with a reason from
  the bounded set.
- **Never implement focus as a selection filter.** It is a lens applied after selection.
- **Never suppress a finding under a focus.** Reorder, never drop.
- **Never emit a finding without a basis**, and never phrase a slice observation as store-wide.
- **Never let a consolidation discard an origin**, and never present re-captures as corroboration.
- **Never compress away a condition to fit a budget.** Narrow and report; a claim shorn of its
  exception is false.
- **`kind = understanding` is a kind like any other** — same selection, citation, reconciliation,
  focus and confidentiality (LADR-15). Its load/import is a separate capability owned by
  `mimisbrunnr-kvasir-understanding`, not this skill.
- **`near-miss-tag` is evidence-only** (LADR-10). Write the shared
  `mimisbrunnr-odin-context-memory/scripts/near_miss_tags.py` helper's input to
  `.context/mimisbrunnr-saga-dossier/scratch/near-miss.json` and pass it as `compose
  --near-miss-evidence`; the helper validates it and its findings name their supporting memory. A
  near-miss-tag written into `--judgements` is refused, as is one naming no memory. No evidence
  means no finding; no tag-graph or full-dossier completeness claim is made.

## Base URL / token

The preview and the bundle are requested from the Host API. The base URL is read from `CONTEXT_MEMORY_BASE_URL`
(default `http://localhost:5141`) and the read token from `CONTEXT_MEMORY_READ_TOKEN`, both seeded at
import from the machine credential file (`~/.mimisbrunnr/credentials`) — read token and base URL only;
the write token is never loaded, and a write token present in the environment refuses the bundle
request (read-only, LADR-08 / NFR-06). A missing read token is a `missing-credential` error naming the
variable and the file, never an unauthenticated request that gets 403. The token value never appears in
a committed file (skill-secret-handling). `--base-url` overrides for a one-off. The base must be a bare
http(s) loopback origin (`localhost`, `127.0.0.1` or `::1`) — userinfo, a path, `;params`, a query or
a fragment is refused, and a trailing slash is ignored.

## Scripts

| Script | Purpose |
|---|---|
| `scripts/dossier_composer.py` | Read-only preview + bundle requests and dossier composition (ordering, consolidation, lifecycle, citation, findings, focus, reconciliation), and the `near_miss_findings()` helper |

## Test

Committed harness: `python3 -B .agents/skills/mimisbrunnr-saga-dossier/tests/run_tests.py` (125 tests).

## Related

- `docs/hlds/005-contextual-export/` — design (LADRs, NFRs), the determinism boundary, the contract.
- `.agents/skills/mimisbrunnr-odin-context-memory/` — the capture skill (sole **writer**). This dossier
  skill is a reader; do not conflate the two. Reuses its `near_miss_tags.py` helper.
- `.agents/skills/mimisbrunnr-kvasir-understanding/` — the load/transfer sibling (HLD-007); owns the
  Understanding kind's load/import, which this read-only skill does not.
