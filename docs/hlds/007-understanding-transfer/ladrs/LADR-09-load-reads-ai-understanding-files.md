# LADR-09: Load and import read the ai-understanding file format as structured input

**Status:** Accepted

## Context

- The `ai-understanding` skill writes Understandings to disk as `<subject>-<yyyyMMdd-HHmm>/<slug>.understanding.md`:
  YAML frontmatter (`question`, `description`, `scope`, `confidence`, `provenance`) and `## Answer` /
  `## Why` / `## Boundaries` sections. A slug repeated across stamped folders is a version chain, newest current.
- Under LADR-06 that file was foreign prose. `load` printed the frontmatter raw, and `import --store`
  turned the frontmatter block into a candidate fact, beside an answer flagged as a possible bundle.
- A folder was unreadable: `load <folder>` raised `IsADirectoryError`. That included a dump folder,
  even though LADR-07 says another session loads "that folder".

## Decision

`load` and `import` recognise a third input: an ai-understanding unit (detected by the
`.understanding.md` postfix or a `slug` in frontmatter; forced with `--format understanding`). Its parts
map onto the LADR-04 fields: `question` (else `description`) → `Description`, `## Answer` → `Statement`,
`## Why` → `ContentSummary`, `provenance.learned` → `ValidFrom`. Prose `## Boundaries` has no date to
live in `ValidUntil`, so an import appends it to `ContentSummary`. `confidence` and the unit's
portability `scope` travel as advisory payload keys for the capture path, not as stored columns.

A folder input resolves to every `*.understanding.md` beneath it, taking the newest version of each
slug (folder stamp, then `updated`) and reporting how many older versions were passed over. A folder
with no units but a `_session.md` loads that dump. Anything else is refused with exit `2`.

## Alternatives Considered

- **Keep treating the file as foreign prose.** Rejected: it captured frontmatter as a fact, and dropped
  the question, confidence and provenance that make the unit worth loading.
- **Map the unit's `scope` onto the group scope.** Rejected: `portable | repo-specific` is portability,
  not a `ScopeDimension`; conflating them would mis-scope the group. Cross-repo reach stays LADR-01's default.

## Consequences

- One file format moves between the two Understanding skills with its structure intact.
- This is an input reader, not a store: LADR-01's "no new file convention" is about where an
  Understanding is *stored*, and that is still the memory table.
- The frontmatter parser covers the flat subset ai-understanding writes, not general YAML.
