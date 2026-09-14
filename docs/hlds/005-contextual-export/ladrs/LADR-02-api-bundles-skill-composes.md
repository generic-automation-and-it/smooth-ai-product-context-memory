# LADR-02: The API assembles the bundle; the skill composes the dossier

**Status:** Draft

## Context

Ordering a slice so reasoning reads as reasoning, collapsing three restatements into one claim,
noticing that two current memories cannot both be true, and judging that a slice is missing something
are all judgement. None can be expressed as a predicate over stored columns.

The existing architecture already answers where judgement lives, and states it as a non-negotiable:
**no LLM in Application or Host; the skill owns judgement.** The write path is built that way — the
skill decides new-versus-version-bump, derives links and generates summaries, and the API persists a
decision that has already been made.

The temptation here is stronger than on the write path, because "export a document" sounds like a
server responsibility and the output is a file.

## Decision

**Split** the capability at the judgement boundary. The API assembles a **bundle**: the selected
memories with their bodies, the edges between them with their reasons, and a manifest of what was
selected, reached and cut. The skill composes the **dossier**: ordering, collapsing, supersession
marking, citation, and the findings.

The API therefore performs selection, hydration and reporting — all mechanical, all reproducible, all
expressible as SQL and Cypher over one session. It makes no claim about what the slice means. The
skill performs every act of interpretation and produces the artefact.

This also places the reproducibility guarantee where it can actually be held. Selection is mechanical,
so it can be required to be byte-identical (NFR-02). Composition is judgement, so it cannot be — and a
guarantee spanning both would either forbid the judgement or be untrue.

## Alternatives Considered

- **Compose server-side, calling a model from Host or Application** — rejected: contradicts the standing architectural non-negotiable, and puts an unbounded-latency external dependency behind an HTTP read.
- **Compose server-side without a model, using heuristics** — rejected: heuristic ordering and string-similarity dedup produce something that looks composed and is not, and it would then be trusted as such. Contradiction detection is not reachable by heuristic at all.
- **Skill fetches through existing query and traversal endpoints and assembles the bundle itself** — rejected: N round trips where one composed statement suffices, selection logic duplicated in the skill where it cannot be tested against the database, and reproducibility becomes untestable.
- **Return the bundle only, and leave composition to whatever consumes it** — rejected: the findings are the point (`BR-27`–`BR-29`), and they need judgement. A bundle alone is a concatenation, which BRD-002 explicitly rejects as worse than the store.

## Consequences

- The reproducibility line falls in the one place it can be enforced.
- The API stays free of model dependencies, so a bundle is available offline even when composition is not.
- Composition cost is the skill's and is visible to the practitioner as such — which is what makes `BR-32` implementable.
- The bundle is a new wire contract with its own cost: it must carry bodies, edges, reasons and a manifest, and it is larger than any existing response. Capped by NFR-03.
- Two artefacts exist for one request (bundle, then dossier). Only the dossier is the deliverable; the bundle is an intermediate and must not become a document people read.

## Related

- **LADR-08** — which skill composes, and why it is read-only.
- **NFR-02** — reproducibility, which applies to the bundle and deliberately not to the dossier.
