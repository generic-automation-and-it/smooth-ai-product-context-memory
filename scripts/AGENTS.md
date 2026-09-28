# AGENTS.md - Operational Scripts

## TL;DR

Operational checks support operator decisions; a preflight must never repair the corpus it inspects.

## Non-Negotiables

- Ticket ownership preflight is read-only, including against the default dev database. Never weaken the migration's duplicate guard or choose a keeper automatically.
- Exact provider/key and group UUID reports are sensitive operational output, not application logging. Never send them to telemetry, CI artifacts, or commits; credentials stay inside the container.
- API smoke credentials are generated per run, passed through environment variables, used only in
  Authorization headers, and never printed or committed.
- Verification uses a newly created isolated fixture, never the default corpus or a schema cloned from it. Cleanup may remove only resources created by that test invocation.
- Do not turn the manual remediation runbook into an automated destructive cleanup script. Membership ownership is an operator decision; preserving memories does not preserve ticket-based discoverability.

## Key Behaviors

- `release_policy.py` permits publication only for `push` on `refs/heads/main`. PRs, tag pushes, releases, and manual dispatch fail closed. Candidates include source SHA/run/attempt; stale main builds cannot promote `latest`. No version-tag or GitHub Release creation path remains.
- A clean snapshot is not a reservation: writers must remain stopped from the final precheck through migration. Host startup applies migrations, so checking after startup is too late.
- Same-group repeated memberships are not cross-group ownership conflicts. Malformed containers or identity types block preflight even though some live reads treat them as absent; silently skipping them would claim migration readiness without establishing it.
- The checker needs only `public.memory_group`, not AGE or the ticket migration. The [ticket migration runbook](../docs/hlds/003-graph-edges-on-age/ticket-migration-runbook.md) targets legacy data before graph backfill; it refuses installed/partially installed ticket graph objects.
- The one-shot `restore` verb's built-in reconciliation supersedes `scripts/verify-graph-restore.sh` for the HLD 001 NFR-03 round-trip: it restores both stores from a self-verifying snapshot archive and prints a reconciliation (counts, blob resolution, bounded traversal) instead of `pg_dump`/`pg_restore` around `docker exec`. `verify-graph-restore.sh` remains for a lighter graph-only round-trip and `seed-graph-sample.sh` still seeds the NFR-03/NFR-04 checks; the separate NFR-04 pre-upgrade check (`verify-graph-preupgrade.sh`) is unaffected.
- `provision-credentials.sh` writes the API Bearer tokens to **two** gitignored env files, split by parser grammar: `.context/mimisbrunnr.env` is sourceable and carries only valid shell identifiers (the server `ApiAccess__*` names and the skill `CONTEXT_MEMORY_*` names), and `.context/mimisbrunnr.env.controller` carries the `Parameters__*` names for the published controller's `--env-file` — those names are not shell identifiers, so keeping them in the sourceable file makes `set -a && source` print a token as "command not found". Both files carry the same token values, so the service and the host-side skills still read one source of truth. Idempotent once the pair exists: re-runs reuse the existing values, `--rotate` regenerates, and a pre-split single file (no `.controller`, or still carrying `Parameters__*` lines) is rewritten into the pair rather than silently kept. Mode 600. The tokens are runtime configuration only and never touch the store, so rotating them invalidates no data.

## Test References

- Release policy: `python3 scripts/test_release_policy.py`; negative event/ref matrix, fail-closed identity, and stale-main alias tests. Runs in the PR gate's "Test release policy" step.
- Operational harness: `python3 scripts/tests/test_ticket_ownership.py`; mocked Docker transport checks require no daemon.
- Credential provisioner: `bash scripts/test-provision-credentials.sh`; runs the provisioner into a scratch `--env-file` and asserts that sourcing it prints nothing (no token as "command not found"), that `CONTEXT_MEMORY_*` is exported, that the sourceable file carries **no** `Parameters__*` line, and that the `.controller` file carries both `Parameters__api-*-token` names. Runs in the PR gate's "Test credential provisioner" step. Covers the create path only.
- Real PostgreSQL checks: `python3 scripts/tests/test_ticket_ownership.py --postgres`; disposable pinned AGE container, synthetic pre-migration table only, no published ports or corpus access. Not part of the .NET suite.
- 2026-09-15 verification: 14 tests passed (transport, read-only enforcement, exact/malformed ownership, runbook rollback/commit/stale-input guards and partial-graph refusal); Dockerized ShellCheck (`koalaman/shellcheck:v0.11.0`, read-only mount, network disabled) and Bash syntax checks passed. This does not establish whole-migration or application-suite acceptance.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-27 | `provision-credentials.sh` re-runs now also rewrite the pair when it is inconsistent — a pre-split single file (no `.controller`, or still carrying `Parameters__*` lines) — so upgrading from the earlier layout gets the split instead of silently keeping the file whose `source` prints a token and whose `--env-file` does not exist. `smoke-apphost-container.sh`'s `probe_health` now takes the body on stdout and splits the status off it, so the body survives the containerized `curl` path (`-o` wrote into a `--rm` container and was discarded). `scripts/AGENTS.md` Key Behaviors and Test References updated to the shipped two-file contract, the new harness and the create-path-only coverage. | pre-MVP review loop |
| 2026-09-27 | `provision-credentials.sh` splits the credential file: the sourceable `mimisbrunnr.env` carries only valid shell identifiers (`ApiAccess__*`, `CONTEXT_MEMORY_*`), and the `Parameters__*` names — not valid shell identifiers — move to `mimisbrunnr.env.controller` for the published-controller `--env-file`, so a `source` no longer prints a token as "command not found". Added `test-provision-credentials.sh`, wired into CI. | batch4 credential safety |
| 2026-09-27 | `smoke-apphost-container.sh` health waits now emit the controller log, the API container log and the last HTTP status + body on a non-200, so a 404/500/refused connection no longer collapse into one indistinguishable "did not become healthy" line. | batch4 release unblock |
| 2026-09-27 | `provision-credentials.sh` now also writes the AppHost user secrets (`Parameters:api-read-token` / `api-write-token`) so Aspire injects the same values the skills hold, closing the AppHost run-mode gap; a `--skip-apphost` flag escapes it for SDK-less environments. | batch3 review |
| 2026-09-27 | Added `provision-credentials.sh`, a one-command API credential provisioner that writes both the server `ApiAccess__*` and skill `CONTEXT_MEMORY_*` token name forms to a gitignored, mode-600 env file — removing the manual dashboard-copy step from the first-run path. | batch3 |
| 2026-09-24 | `verify-graph-restore.sh` restore-verification role superseded by the one-shot `restore` verb's built-in reconciliation (HLD-006). `verify-graph-preupgrade.sh` (NFR-04 pre-upgrade) unaffected. | HLD-006 |
| 2026-09-21 | Added npm package smoke coverage that packs and installs the tarball in isolation, then executes every public CLI help path. | npm skill distribution |
| 2026-09-17 | Release smoke now generates separate read/write API tokens and exercises routes with least-capability credentials. | HLD-002 NFR-04 |
| 2026-09-16 | Added the release-policy harness to Test References. | PR #65 review |
| 2026-09-16 | Restricted release policy to main pushes and added negative event/ref tests; removed tag/manual publication paths. | PR #65 |
| 2026-09-15 | Added read-only exact ticket ownership preflight, malformed-data blockers, isolated operational tests and operator-only remediation runbook. Migration fail-closed duplicate guard unchanged. | PR #63 finding 3; HLD-003 LADR-08 |
