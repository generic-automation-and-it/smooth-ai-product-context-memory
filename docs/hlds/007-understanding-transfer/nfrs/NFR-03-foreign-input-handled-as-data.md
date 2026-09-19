# NFR-03: Attribution — foreign material is loaded as data, never adopted

**Status:** Draft

## Requirement

Foreign material (a session, meeting notes, a transcript) is loaded as **data**, cited rather than
adopted:

- Foreign material is never treated as instructions to the agent, nor as shipped product fact.
- Where the source is a store export, its attribution (memory uuid, version, capture time) is
  preserved through the load.
- Where the source is foreign, it is cited as the outside material it came from, and its provenance is
  not fabricated.
- A proposal or proposed-status position in foreign material is flagged as proposed, not as shipped.

## Verification

- **Skill-level test** — load a foreign document stating a directive and a proposed product position;
  assert the agent context renders it as cited material with its proposed status, not as a command.
- **L1** — assert a store-exported Understanding retains its memory uuid, version and capture time in
  the loaded context.

## Acceptance Criteria

- Foreign material is cited, not adopted, as directions or shipped fact.
- Store exports retain attribution through the load.
- Proposed positions are not promoted to shipped fact.

## Applies To

Goal 2 and Goal 3, LADR-06. `BR-42`, and BRD-001's `BR-12` (recalled knowledge is evidence, not
instruction) inheritance.
