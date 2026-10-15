# AGENTS.md - mimisbrunnr-heimdallr-find-session-metadata

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — what the reporter prints, its withholding rules and its console-only contract live there.

## Non-Negotiables

- **The JSON field names are a contract with three readers.** `mimisbrunnr-kvasir-understanding`
  (`understanding_client.py`), `mimisbrunnr-saga-dossier` (`dossier_composer.py`) and
  `mimisbrunnr-ymir-bootstrap` (`rootMatches`) read `ticketsWithheld`, `repositoryWithheld`,
  `rootMatches`, `initiative` and the `*Unavailable` fields from `--json`. A rename here reads as
  "absent" there and fails nothing in this skill's harness.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-10-15 | Ralph loop: `REFUSED_DIRS` (never read) removed and `parse_repo` simplified | — |
| 2026-10-03 | `provider:KEY` no longer re-emits `local:KEY` | — |
| 2026-10-03 | Repository keeps every path segment (`group/subgroup/repo`), not the last two | — |
| 2026-10-03 | Autofill is outbound only | — |
| 2026-10-03 | Commit window pinned to the branch ref, so the same branch state yields the… | — |
| 2026-10-03 | Default autofill source for the labelling skills (`--heimdallr false` opts out) | — |
| 2026-10-05 | Credential-shaped `word:value` candidates are withheld, not reported as tickets | #182 |
| 2026-10-05 | Tickets in one subject are reported in first-seen order | #179 |
| 2026-10-06 | A checkout outside the home folder is withheld (`rootWithheld`) | #190 |
| 2026-10-06 | Root shown from `~`, withheld when credential-shaped or unredactable | #188 |
| 2026-10-06 | Origin path passes the redactor | #186 |
| 2026-10-06 | Branch name passes the redactor (`fix/password=…` leaked) | #184 |
| 2026-10-07 | README checks `rootMatches`, not the withholdable `root` | — |
| 2026-10-07 | `SKILL.md` checks `rootMatches`, not `root` (display only, `null` when withheld) | #200 |
| 2026-10-14 | The reporter moved to the shared script folder (HLD-010 B2) | #222 |
| 2026-10-15 | CI claim scoped to upstream's PR gate, with the manual command for a vendoring… | — |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
