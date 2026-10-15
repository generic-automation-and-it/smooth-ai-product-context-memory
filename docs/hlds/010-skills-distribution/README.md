# Shared Skill Code Distribution — High-Level Design

| | |
|---|---|
| **Status** | Accepted — implemented |
| **Owner** | generik0 |
| **Tracker** | [issue 222](https://github.com/generic-automation-and-it/smooth-ai-product-context-memory/issues/222) (Part B) |
| **Last updated** | 2026-10-14 |

> Discovery / prototyping HLD. This document delivers **intent + spec** — what we are
> building and why, the decisions behind it, and the quality bar it must meet. It does
> **not** contain an implementation plan; execution (phasing, sub-issues, sequencing) is
> tracked in the issue/work tracker.
>
> **Accepted — implemented** (2026-10-14): the B2 move landed. `_mimisbrinnr-shared/scripts/`
> exists and holds the shared code, the three peer reaches are gone, `ai-understanding`'s
> parity harness compares against the shared copy, `package.json#files` carries the folder,
> and the PR gate greps for a reintroduced peer path (NFR-01).

## Intent

Code used by more than one skill currently travels two ways: some skills reach into a
peer's `scripts/` folder by sibling path, others carry byte copies (WET) held together by
parity tests. Both work, but the mix means every shared fix must answer "which copies,
which references" from scratch — the Part A gate fix touched two identical files and proved
a third consumer (kvasir) only by a separate regression test, because kvasir runs odin's
copy by reaching across the skills boundary.

This design fixes one model: inside the `mimisbrunnr` plugin, shared code lives in one
folder every skill references and no skill reaches into a peer; across plugins, WET copies
with parity tests. The move changes where code lives, never what it does — the capture
contract, its sole writer, and its read/write capability split are untouched.

## Key Goals

### 1. One shared folder inside the plugin; no skill reaches into a peer

Every script used by more than one `mimisbrunnr` skill moves to
`.agents/skills/_mimisbrunnr-shared/scripts/`. A skill references only that folder — never
`parents[2] / "mimisbrunnr-odin-context-memory"` or any other peer. The folder carries no
`SKILL.md`: it is not a skill and must never be loaded as one. No compatibility shims are
left at the old paths; a stale reference fails loudly at its call site rather than running
a second copy.

**Acceptance criteria / DoD**

- `grep -rn 'parents[2] / "mimisbrunnr-' .agents/skills/*/scripts` returns nothing.
- `_mimisbrunnr-shared/` holds no `SKILL.md`, and no skill loader lists it.
- Every moved consumer resolves by the shared path under each skills root
  (`.agents/skills`, `.claude/skills`, `.codex/skills`, npm layout).

### 2. WET copies with parity across plugins

`ai-understanding` is a separate plugin and upstream-owned; it must install and run where
no `mimisbrunnr-*` skill exists. It keeps its self-contained copies (`decisions_gate.py`,
`decisions_rubric.json`, `redact.py`, `session_marker.py`) and its parity test, extended to
compare against the shared copy. A fix to shared logic lands in the shared folder and is
re-copied outward — the copy direction is always shared → plugin, never the reverse.

**Acceptance criteria / DoD**

- The parity harness compares the shared copy against every WET copy and is green.
- `ai-understanding` installs and runs standalone (no `mimisbrunnr-*` present).
- No new WET copy is added inside the `mimisbrunnr` plugin.

### 3. The capture-contract invariants survive the move

Odin stays the documented owner of the capture contract (sole authority on the write
path, HLD-002); only the code location moves. There is still exactly one read client and
one write client in the plugin, and the read client still refuses to start with a write
token present in any spelling. The store-side capability check is unchanged. Widening WET
coverage to the sole writer or the refusal boundary is out of scope: a security boundary
held only by parity tests is a second implementation, not a second copy.

**Acceptance criteria / DoD**

- Exactly one read client and one write client exist in the plugin.
- The read client exits non-zero naming the variable (never its value) with any
  write-token spelling present; the existing refusal tests are green unchanged.

### 4. Every distribution route carries the shared folder

A shared folder that one route drops is a latent split-brain: the skill works in the
checkout and breaks installed. Marketplace install copies the whole repository (measured
2026-10-14, B0); npm ships an allowlist (`package.json` `files`); `.claude/skills` and
`.codex/skills` resolve to the same files as `.agents/skills` (same inode). The move
updates each route's manifest so all three keep carrying the folder.

**Acceptance criteria / DoD**

- A marketplace install contains the shared scripts beside the skills.
- A packed npm tarball lists the shared folder, and the launchers run an import through it.
- The mirrors resolve the shared path with no copy step.

## Core Separation of Concerns

> Location is packaging; ownership is contract.

Moving a script changes which routes must carry it and which paths reference it. It does
not change who owns the behaviour, who may write to the store, or what a refusal means.
Every decision below is judged against that split: if a change would need a second owner
or a second refusal boundary, it is out of scope.

## Guiding Principle — One copy, one owner, every route.

> Share by reference inside the plugin, by copy across it — and never a third way.

- Shared logic has exactly one owner (odin for the capture contract) and one canonical path.
- What we will deliberately NOT do: duplicate the sole writer or the read client's
  write-token refusal into WET copies; leave shims at the old peer paths; merge
  `ai-understanding` into the shared folder.

---

## Diagrams

- [System Context (C1) + container view](./diagrams/c4-context.md)

## Architecture Decisions (LADRs)

LADRs 01–02 are strategic (*what* and *why*); LADR-03 is the ownership invariant the move
must preserve. Each is a single decision — a horizontal concern spanning this HLD. See
[`./ladrs/`](./ladrs/).

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-shared-folder-inside-the-plugin.md) | Shared scripts live in `_mimisbrunnr-shared/scripts/`; no skill reaches into a peer | Accepted |
| [LADR-02](./ladrs/LADR-02-wet-copies-across-plugins.md) | Across plugins, WET copies with parity tests; `ai-understanding` stays standalone | Accepted |
| [LADR-03](./ladrs/LADR-03-odin-owns-the-contract.md) | Odin stays the documented owner; one read client, one write client, refusal intact | Accepted |

## Non-Functional Requirements

Each NFR is a horizontal quality concern spanning the whole design, with a measurable
target, a verification mechanism, and acceptance criteria. See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-no-peer-reaches.md) | Coupling | Zero peer-skill path references in shipped scripts | Accepted |
| [NFR-02](./nfrs/NFR-02-copy-parity.md) | Consistency | Every WET copy byte-identical to the shared source; parity harness green | Accepted |
| [NFR-03](./nfrs/NFR-03-client-singularity-and-refusal.md) | Security | Exactly one read + one write client; read refuses any write-token spelling | Accepted |
| [NFR-04](./nfrs/NFR-04-shared-folder-on-every-route.md) | Portability | Marketplace, npm, and both mirrors all resolve the shared folder | Accepted |
