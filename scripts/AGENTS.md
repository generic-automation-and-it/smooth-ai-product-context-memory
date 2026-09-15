# AGENTS.md - Operational Scripts

## TL;DR

Operational checks support operator decisions; a preflight must never repair the corpus it inspects.

## Non-Negotiables

- Ticket ownership preflight is read-only, including against the default dev database. Never weaken the migration's duplicate guard or choose a keeper automatically.
- Exact provider/key and group UUID reports are sensitive operational output, not application logging. Never send them to telemetry, CI artifacts, or commits; credentials stay inside the container.
- Verification uses a newly created isolated fixture, never the default corpus or a schema cloned from it. Cleanup may remove only resources created by that test invocation.
- Do not turn the manual remediation runbook into an automated destructive cleanup script. Membership ownership is an operator decision; preserving memories does not preserve ticket-based discoverability.

## Key Behaviors

- A clean snapshot is not a reservation: writers must remain stopped from the final precheck through migration. Host startup applies migrations, so checking after startup is too late.
- Same-group repeated memberships are not cross-group ownership conflicts. Malformed containers or identity types block preflight even though some live reads treat them as absent; silently skipping them would claim migration readiness without establishing it.
- The checker needs only `public.memory_group`, not AGE or the ticket migration. The [ticket migration runbook](../docs/hlds/003-graph-edges-on-age/ticket-migration-runbook.md) targets legacy data before graph backfill; it refuses installed/partially installed ticket graph objects.

## Test References

- Operational harness: `python3 scripts/tests/test_ticket_ownership.py`; mocked Docker transport checks require no daemon.
- Real PostgreSQL checks: `python3 scripts/tests/test_ticket_ownership.py --postgres`; disposable pinned AGE container, synthetic pre-migration table only, no published ports or corpus access. Not part of the .NET suite.
- 2026-09-15 verification: 14 tests passed (transport, read-only enforcement, exact/malformed ownership, runbook rollback/commit/stale-input guards and partial-graph refusal); Dockerized ShellCheck (`koalaman/shellcheck:v0.11.0`, read-only mount, network disabled) and Bash syntax checks passed. This does not establish whole-migration or application-suite acceptance.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-15 | Added read-only exact ticket ownership preflight, malformed-data blockers, isolated operational tests and operator-only remediation runbook. Migration fail-closed duplicate guard unchanged. | PR #63 finding 3; HLD-003 LADR-08 |
