# npm distribution

## TL;DR

Source-controlled npm launchers live under `npm/cli/`, not root `bin/`; .NET's standard `**/[Bb]in/*`
ignore remains untouched. `package.json` owns the publish allowlist and public command mapping.

The launchers resolve packaged scripts relative to the package root, so a script that moves must move
its `files` entry in the same change. Under [HLD-010](AGENTS.md) (LADR-01) that is now one folder,
`.agents/skills/_mimisbrunnr-shared/scripts/`, holding every script more than one skill uses; the
`mimisbrunnr-odin-context-memory` bin points into it.

## Non-Negotiables

- Keep every public launcher in `package.json#bin`; each file starts with `#!/usr/bin/env node`.
- Resolve packaged resources relative to the launcher, never caller working directory.
- Keep `package.json#files` as the package allowlist; do not add a parallel `.npmignore` blocklist.
- Verify the packed artifact through `npm test`; source-tree execution alone does not prove publication works.
- The Python floor lives in `MIN_PYTHON` (`npm/cli/_run.js`) and is **checked**, not documented: every
  launcher refuses an older interpreter before spawning a client, because the failure it prevents is a
  `SyntaxError` or `AttributeError` from deep inside a client on 3.8. `3.9` is the floor, not 3.11 —
  `fromisoformat` accepts a limited fractional-digit count before 3.11 and the clients normalise the
  fraction themselves. Any message naming a version derives from the constant, never from a literal;
  `package.json#mimisbrunnrPython` is a declarative mirror of the same floor that nothing reads, so raise
  it in the same change. The floor is enforced only on the npm launchers — a direct
  `python3 <skill>/scripts/*.py` invocation (Claude/Copilot plugin and skill path) bypasses it.
- `package.json#files` ships the **shared script folder** plus the two skill `scripts/` folders whose clients resolve each other and the shared code by path: kvasir's client runs the shared redactor/atomicity/clients and the shared heimdallr reporter, and the reporter screens tickets with the shared `redact.py`. Dropping the shared folder does not break heimdallr's exit code — it reports `ticketsUnavailable` and no tickets — so `npm test` runs the packed reporter with `--json --repo-root <checkout>` and asserts `ticketsUnavailable` is `null`. `mimisbrunnr-odin-context-memory/scripts/` is still listed for the two composition helpers odin alone uses (`authority.py`, `divergence.py`).
- **The shared folder's entry excludes its own `__pycache__`, and the two older entries still do not.** A developer who runs the skill harnesses and then packs ships `__pycache__/*.pyc` compiled on their machine; CI, packing from a clean checkout, ships none. The exclusion is a `files` negation rather than a `.npmignore` blocklist (the Non-Negotiable above forbids the latter), and it is scoped to the folder this change introduces — the pre-existing behaviour of the other two entries is left untouched rather than silently widened. Recorded so the asymmetry is not read as an oversight; extending it to all three is a separate, deliberate change.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-14 | **The shared script folder replaced the three sibling `scripts/` entries (HLD-010 B2).** `package.json#files` now allowlists `.agents/skills/_mimisbrunnr-shared/scripts/`; the odin bin and the dispatcher's `context-memory` entry point into it (the write client moved), and `scripts/npm-smoke.js` resolves the packed reporter there too. `mimisbrunnr-odin-context-memory/scripts/` stays listed for `authority.py` and `divergence.py`, which odin alone uses. The `--json --repo-root` redactor-resolution assertion is unchanged and still the check that catches a dropped allowlist entry. The shared folder's entry also carries a `__pycache__` negation — a `files` exclusion, not a `.npmignore` blocklist — scoped to the folder this change introduces; `npm pack --dry-run` was checked with and without it (4 `.pyc` lines → 0). | HLD-010 LADR-01, issue 222 |
| 2026-10-05 | Recorded the sibling-path dependencies inside the package (kvasir → odin + heimdallr, heimdallr → odin's `redact.py`, the last new in issue 182) and added a packed-heimdallr `--json` run to `scripts/npm-smoke.js`: it asserts `ticketsUnavailable` is `null`, because a missing redactor fails closed with exit 0 and only that field shows it. Mutation-checked: excluding `redact.py` from `files` (with the bin loop disabled, since odin's `--help` imports it first) fails the new assertion. | issue 182 |
| 2026-09-29 | Recorded the checked Python floor, which shipped without any statement in the file that owns `npm/cli/`: `MIN_PYTHON` in `_run.js` is the single enforced minimum (3.9), every public launcher checks it before spawning a client, and a maintainer editing it or adding a launcher needed a local record of the reason and of the `package.json#mimisbrunnrPython` mirror. The could-not-determine message now derives its version from the constant instead of repeating the literal. | package distribution |
| 2026-09-21 | Established isolated npm launcher ownership and installed-tarball smoke verification. | package distribution |
