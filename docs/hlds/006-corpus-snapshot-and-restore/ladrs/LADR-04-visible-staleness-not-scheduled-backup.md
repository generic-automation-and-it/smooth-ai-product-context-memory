# LADR-04: Visible staleness, never a scheduled backup

**Status:** Draft

## Context

BR-01 forbids designs that depend on remembered maintenance, and BRD-002 rejected standing
regeneration on the same grounds. A backup regime that requires the practitioner to remember a
schedule is upkeep, and upkeep is the recorded failure mode of every prior durable-knowledge
attempt. Yet a backup nobody takes protects nothing.

## Decision

**Replace** recurrence with visibility. A read-only preflight check reports the age of the most
recent snapshot alongside corpus counts and the last walk's orphan/dangling numbers. Staleness
becomes a stated fact the practitioner encounters in the course of normal operation — not an
alert, not a scheduled job, not a prompt injected into capture.

BR-01 governs knowledge maintenance; this design holds that asset protection is operations, not
knowledge upkeep — but it still refuses scheduling, because the distinction is arguable and the
cost of honouring BR-01's spirit is one line of reported age.

## Alternatives Considered

- **Scheduled snapshots (cron, systemd timer)** — recurring infrastructure the practitioner must own; fails BR-01's spirit and adds an unattended write path to a sensitive artefact.
- **Snapshot as a capture side effect** — couples the cheapest operation (capture) to the most expensive (full corpus walk), violating BR-02's no-interruption clause at exactly the checkpoint moment.
- **Do nothing** — the status quo the BRD's own context records as a gap.

## Consequences

- No recurring task exists anywhere in the design; BR-01 is honoured structurally.
- Durability depends on the practitioner acting on visible staleness — accepted honestly: this design makes neglect informed, not impossible.
- The preflight check is also the natural home for the version-pairing pre-upgrade posture (HLD 003 NFR-04), unifying operational self-checks in one place.
