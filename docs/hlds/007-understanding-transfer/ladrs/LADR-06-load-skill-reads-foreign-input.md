# LADR-06: The load skill accepts arbitrary external input, loaded as data

**Status:** Draft

## Context

BRD-003 BR-42 requires the load capability to accept not only store-native Understanding exports but
any session, meeting notes or transcript. The instinct is to special-case each input shape.

But the input substance is the same: unstructured prose that has to become grounding context, and
possibly (under `--store`) a set of atomic facts to capture. Special-casing sessions vs meeting notes
vs transcripts multiplies the surface for no benefit and invites a static type per input kind.

There is also a safety line: foreign material must never be adopted as instructions or as shipped
product fact. A session transcript is not commands, and a note is not evidence of what shipped.

## Decision

The load skill accepts a single, general input — a document (store-native export or arbitrary
external prose) — and treats it uniformly as **data**. It injects the material into the agent's context
as cited grounding. Where the source is a store export, its attribution (memory uuid, version, capture
time) is preserved; where it is foreign, it is cited as the outside material it came from.

Foreign material is loaded as data, never adopted as directions or as shipped behavior. Atomicity and
redaction apply only when `--store` is passed, through the capture path (LADR-03).

## Alternatives Considered

- **A distinct consumer per input kind (session, meeting, transcript)** — rejected: multiplies the
  surface; the handling is the same.
- **Treat foreign material as trusted store content** — rejected: imported material is not adopted as
  shipped fact; that would be a confidentiality and correctness failure (NFR-03).
- **Refuse anything that is not a store export** — rejected: rejects BR-42's whole point.

## Consequences

- One load path handles store exports and foreign documents.
- Foreign material is always cited, never adopted (NFR-03).
- Only the `--store` path applies atomicity/redaction, so foreign material that is merely loaded (the
  default) is not quietly decomposed.
