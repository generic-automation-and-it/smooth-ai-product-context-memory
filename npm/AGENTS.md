# npm distribution

## TL;DR

Source-controlled npm launchers live under `npm/cli/`, not root `bin/`; .NET's standard `**/[Bb]in/*`
ignore remains untouched. `package.json` owns the publish allowlist and public command mapping.

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

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-29 | Recorded the checked Python floor, which shipped without any statement in the file that owns `npm/cli/`: `MIN_PYTHON` in `_run.js` is the single enforced minimum (3.9), every public launcher checks it before spawning a client, and a maintainer editing it or adding a launcher needed a local record of the reason and of the `package.json#mimisbrunnrPython` mirror. The could-not-determine message now derives its version from the constant instead of repeating the literal. | package distribution |
| 2026-09-21 | Established isolated npm launcher ownership and installed-tarball smoke verification. | package distribution |
