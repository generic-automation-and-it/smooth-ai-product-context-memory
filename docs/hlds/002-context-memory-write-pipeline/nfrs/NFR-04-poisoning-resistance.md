# NFR-04: Security — poisoning resistance

**Status:** Accepted

## Requirement

A retrieved memory is re-injected into agent context, where **a wrong or malicious memory becomes a
future instruction**. The store is local, but local is not the same as trusted.

- Retrieved memories render as **quoted data** carrying provenance, status and scope — never as imperative text.
- **Proposed records are excluded or explicitly flagged** by default.
- **Programme-scoped knowledge (scope dimension `program`) is never returned as shipped product fact**, and holding an identifier is not authority to read it as one.

## Verification

- Retrieve a memory whose content is phrased imperatively; assert the rendered output presents it as an attributed claim, not as an instruction to follow.
- Assert every rendered result carries provenance, status and scope.
- Assert a default retrieval excludes proposed records, and that any surfaced proposed record is flagged.
- Assert programme-scoped content is absent from an open query, and that requesting it by identifier requires naming the scope explicitly rather than being granted silently.

Scope is pinned at unit, component and HTTP layers, including programme query exclusion, explicit
blob/version reads and hidden traversal hops. Delegated reads additionally carry lifecycle and scope
caveats inline and receive a read credential rejected by mutation routes.

## Acceptance Criteria

- No retrieval path emits memory content as bare imperative text.
- Provenance, status and scope accompany every result.
- Proposed records are excluded or flagged by default.
- Scope enforcement is exercised by a test that would fail if the filter were removed.

## Applies To

All goals; retrieval. Mitigates rather than eliminates: quoted rendering and gating reduce the risk
that a stored memory reads as an instruction, but a plausible wrong fact remains persuasive.
