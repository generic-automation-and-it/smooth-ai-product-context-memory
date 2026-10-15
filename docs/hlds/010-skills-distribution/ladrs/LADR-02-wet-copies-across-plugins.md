# LADR-02: Across plugins, WET copies with parity tests; `ai-understanding` stays standalone

**Status:** Draft

<!-- Status lifecycle: Draft → Prototype → Accepted. Also: "Superseded by LADR-MM", "Deprecated". -->

## Context

`ai-understanding` ships as its own plugin and is upstream-owned. It must install and run
where no `mimisbrunnr-*` skill exists, so it cannot reference the shared folder — a
reference to a folder outside its plugin is a install-time absence, the same failure peer
reaching has at runtime. It currently carries four copies: `decisions_gate.py`,
`decisions_rubric.json`, `redact.py`, `session_marker.py`. The price of a copy is drift:
the Part A gate fix had to land in both `decisions_gate.py` files, and only a harness
keeps them together.

## Decision

**Keep** `ai-understanding` self-contained with WET copies plus parity tests. The copy
direction is always shared folder → plugin, never the reverse; a fix lands in the shared
source and is re-copied outward. The existing parity harness is extended to compare
against the shared copy, so drift fails with the side that changed named.

## Alternatives Considered

- **Merge `ai-understanding` into the shared folder** — rejected: breaks standalone
  install and the upstream ownership boundary; the skill must survive without this repository.
- **A cross-plugin shared package** — rejected: same install-time absence in a different
  shape, plus a versioning relationship between plugins this repo does not otherwise have.

## Consequences

- `ai-understanding` keeps working installed alone; every shared fix costs one re-copy
  plus a parity run (NFR-02).
- No new WET copy is added inside the `mimisbrunnr` plugin — inside, the rule is LADR-01.

## Related

- **LADR-01** — the inside-the-plugin counterpart (shared folder, not copies).
- **NFR-02** — the parity bar that makes copies safe.
