# npm distribution

## TL;DR

Source-controlled npm launchers live under `npm/cli/`, not root `bin/`; .NET's standard `**/[Bb]in/*`
ignore remains untouched. `package.json` owns the publish allowlist and public command mapping.

## Non-Negotiables

- Keep every public launcher in `package.json#bin`; each file starts with `#!/usr/bin/env node`.
- Resolve packaged resources relative to the launcher, never caller working directory.
- Keep `package.json#files` as the package allowlist; do not add a parallel `.npmignore` blocklist.
- Verify the packed artifact through `npm test`; source-tree execution alone does not prove publication works.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-21 | Established isolated npm launcher ownership and installed-tarball smoke verification. | package distribution |
