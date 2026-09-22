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

## Test References

- Release policy: `python3 scripts/test_release_policy.py`; negative event/ref matrix, fail-closed identity, and stale-main alias tests. Runs in the PR gate's "Test release policy" step.
- Operational harness: `python3 scripts/tests/test_ticket_ownership.py`; mocked Docker transport checks require no daemon.
- Real PostgreSQL checks: `python3 scripts/tests/test_ticket_ownership.py --postgres`; disposable pinned AGE container, synthetic pre-migration table only, no published ports or corpus access. Not part of the .NET suite.
- 2026-09-15 verification: 14 tests passed (transport, read-only enforcement, exact/malformed ownership, runbook rollback/commit/stale-input guards and partial-graph refusal); Dockerized ShellCheck (`koalaman/shellcheck:v0.11.0`, read-only mount, network disabled) and Bash syntax checks passed. This does not establish whole-migration or application-suite acceptance.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-21 | Added npm package smoke coverage that packs and installs the tarball in isolation, then executes every public CLI help path. | npm skill distribution |
| 2026-09-17 | Release smoke now generates separate read/write API tokens and exercises routes with least-capability credentials. | HLD-002 NFR-04 |
| 2026-09-16 | Added the release-policy harness to Test References. | PR #65 review |
| 2026-09-16 | Restricted release policy to main pushes and added negative event/ref tests; removed tag/manual publication paths. | PR #65 |
| 2026-09-15 | Added read-only exact ticket ownership preflight, malformed-data blockers, isolated operational tests and operator-only remediation runbook. Migration fail-closed duplicate guard unchanged. | PR #63 finding 3; HLD-003 LADR-08 |
