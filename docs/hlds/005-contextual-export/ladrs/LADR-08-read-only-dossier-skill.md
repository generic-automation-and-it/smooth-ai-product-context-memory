# LADR-08: A separate read-only dossier skill; `mimisbrunnr-context-memory` narrows to sole *writer*

**Status:** Draft

## Context

Composition is skill work (LADR-02). The question is which skill.

`mimisbrunnr-context-memory` describes itself as both the *sole interface* to the store and the *sole authority on
the write path*. Those are two different claims, and only the second is load-bearing. The first is what
protects the store from uncoordinated writes; the second is a stronger statement that also forbids any
other reader.

The two skills also differ in shape. Capture accumulates silently through a session and writes at an
explicit checkpoint. An export is a single bounded request, invoked deliberately, that writes nothing
(LADR-06). Their switches, their cost profiles and their failure modes have nothing in common — an
export cannot corrupt anything, and its worst failure is a bad document.

## Decision

**Add** a separate read-only dossier skill, and **narrow** `mimisbrunnr-context-memory`'s stated invariant from
sole *interface* to sole **writer**.

The narrowing is the substantive part. "Sole writer" is what actually guarantees the store's integrity:
one path decides new-versus-version, one path derives links, one path applies redaction and produces the
digest. Nothing about that requires readers to be scarce, and reading through the write authority would
mean loading its whole write pipeline to produce a document that writes nothing.

The dossier skill therefore consumes the bundle contract and nothing else. It has no write capability at
all — not gated, not approval-guarded, absent — so LADR-06 and NFR-06 are guaranteed by what the skill
cannot do rather than by what it declines to do.

`mimisbrunnr-context-memory`'s own documentation must be amended when this ships. An unamended "sole interface"
claim would make the new skill look like a violation of it, and the next person would either delete the
skill or quietly ignore the invariant.

## Alternatives Considered

- **Add a `--dossier` mode to `mimisbrunnr-context-memory`** — rejected: loads the write pipeline for a read-only operation, and puts write capability in the hands of a composition pass that must not have it. It also makes the skill's cost profile unpredictable.
- **Keep "sole interface" and route the export through `mimisbrunnr-context-memory` as a pass-through** — rejected: a pass-through that exists only to honour a wording is indirection, and the wording is the thing that is wrong.
- **No skill — compose in whatever agent session asks for an export** — rejected: composition rules (ordering, collapse, findings taxonomy, citation) are a contract. Without a skill they are re-invented per session and no two documents are comparable.
- **Two skills sharing a library of composition rules** — deferred: there is one composing skill, so extracting a shared library now would be abstraction ahead of a second caller.

## Consequences

- Read-only is structural: the composing skill has no write path to misuse.
- `mimisbrunnr-context-memory` keeps the invariant that matters and loses one that was overstated.
- The amendment must land with this work, or the two skills' documentation contradicts itself — precisely the failure this design reports on elsewhere.
- The composition rules become a versioned contract in a skill, so documents are comparable and the rules are reviewable.
- Two skills now touch the store, and a reader must know which is authoritative for what. Stated in both.

## Related

- **LADR-02** — establishes that composition is skill work.
- **LADR-06** — the zero-write rule this makes structural.
- **NFR-06** — verified by capability absence rather than by behaviour.
