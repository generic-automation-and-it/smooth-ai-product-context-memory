# NFR-04: Compatibility

**Status:** Draft

## Requirement

The extension must not become the reason the database cannot be upgraded.

- The database image runs a **Postgres version the extension supports**. At time of writing the extension supports 11 through 18, so no currently-shipping version is excluded.
- **The supported-version ceiling is checked before any Postgres major upgrade**, and the upgrade is blocked — deliberately and visibly — if the extension does not yet support the target.
- The pinned image is **recorded with its Postgres and extension versions**, so the pairing in use is never implicit.
- **Graph objects survive a Postgres minor upgrade** without recreation.

## Verification

- Record the Postgres major version and extension version in the HLD folder at adoption, and update on every change to either.
- A documented pre-upgrade check compares the intended Postgres major against the extension's supported set; this is a checklist item on the upgrade path, not a runtime assertion.
- Restore a backup taken on the current minor into a newer minor, then run a traversal. Failure blocks the upgrade.

## Acceptance Criteria

- The version pairing is written down and current.
- The pre-upgrade check exists as a documented step before the first Postgres upgrade after adoption, not after.
- A minor-version upgrade round-trip passes with traversal intact.
- If the extension ever lags a Postgres major we want, the decision is recorded as an explicit trade — wait, or remove the extension — rather than silently deferring the upgrade.

## Applies To

Goal 4 (operability does not degrade); LADR-01; the database image in both the development and test orchestration hosts.
