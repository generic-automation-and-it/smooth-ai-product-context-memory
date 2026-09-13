# NFR-04: Inspectability

**Status:** Draft

## Requirement

A human can answer *"what does the store actually know?"* **without a database client and without
issuing a query**.

- A **generated** Markdown projection renders groups, memories, current versions and — on request — history.
- Bodies are **reconstructed from their content addresses and inlined**; hashes are never printed as if they were content.
- Relationships are **explicit in file content**, never implied by directory position.
- The projection is **idempotent** — two runs over an unchanged store produce byte-identical output.

## Verification

- Render a seeded store; assert relationships appear as identifiers in the files, not merely as folder placement.
- Run twice without changes; assert byte-identical output.
- Assert bodies are inlined and no bare content address appears where content is expected.
- Remove an object, render, and assert the run warns and continues rather than failing.

## Acceptance Criteria

- Output is generated, never hand-edited, and its directory is excluded from version control.
- Two consecutive runs are byte-identical.
- A missing object produces a warning and a complete render of everything else.

## Applies To

Goal 4; LADR-06. Pays down the opacity consequence that content addressing introduces — a directory of
hashes is no more readable than a table.
