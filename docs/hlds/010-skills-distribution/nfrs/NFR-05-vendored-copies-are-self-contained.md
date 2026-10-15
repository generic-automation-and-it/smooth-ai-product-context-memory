# NFR-05: Vendored copies are self-contained

**Status:** Accepted

<!-- One file per quality attribute. This NFR is a horizontal concern spanning the whole HLD. Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

The installer-owned folders (`_mimisbrunnr-shared`, `ai-understanding`, `ai-brain-dump`, every
`mimisbrunnr-*`) are copied verbatim into other repositories by `scripts/install-skills.sh`, where this
repository's `docs/`, `.github/instructions/` and `.github/workflows/` do not exist. In those folders,
outside table rows (changelog and history entries, which record their date):

- no Markdown link resolves outside `.agents/skills/` — a reference to an HLD, BRD, NFR, rule or the
  skills-root `README.md` / `AGENTS.md` is an absolute upstream URL;
- no rule is cited by its repo-relative path (`.github/instructions/<folder>/…`, `.agents/rules/<folder>/…`)
  — a vendoring repository files its rules under its own folders, so the rule is named by file name with an
  upstream link;
- a claim that a harness is CI-gated names **upstream's** PR gate and gives the command a vendoring
  repository runs itself.

## Verification

A PR-gate step runs `scripts/check_vendored_skills.py`: it walks every `*.md` and `*.yaml` in those
folders, resolves each relative link against its file, and fails the job when one leaves `.agents/skills/`
or when a backticked rule path appears. Its owned set mirrors `is_owned_skill` in
`scripts/install-skills.sh`; `scripts/test_check_vendored_skills.py` runs the installer's own function
against the script's and fails on drift. The CI-claim wording is reviewed per change; it is not
mechanically checkable without false positives.

Measured 2026-10-15: the script passes on the tree; planting `[x](../../../docs/hlds/)` and a backticked
`.github/instructions/skills/…` path into `mimisbrunnr-ymir-bootstrap/README.md` fails it with both hits
named. Python 3.9 and 3.13 agree.

## Acceptance Criteria

- The step passes on the tree and fails on a planted out-of-tree link or rule path.
- A consumer review of a vendored copy raises no dead-link finding for an upstream reference.

## Applies To

Goal 1; every distribution route in NFR-04, and the skills installer, which is the route that puts the
folders in a repository that is not this one. Origin: consumer review 5535929651 (`LEGO/gf-ipc-smart-card`
PR 71) — 15 of its 51 findings were upstream-relative references that are dead in a vendoring repository.
