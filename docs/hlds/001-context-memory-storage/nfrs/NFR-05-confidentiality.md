# NFR-05: Confidentiality

**Status:** Accepted

## Requirement

- **Memory content is never logged.** Not at any level, not in error paths, not in diagnostics. Identifiers, counts and outcomes may be logged; the content they describe may not.
- **Content addresses are not logged at information level**, since an address plus store access is equivalent to the content.
- The **immutability implication is stated wherever it matters**: an object is addressed by its content, so anything written in error cannot be edited out — only orphaned, leaving an address that no longer resolves to wanted content.
- No credentials appear in committed configuration outside the orchestrator's parameter mechanism.

## Verification

- Review every logging call on the write and retrieval paths; assert none takes content as an argument.
- Assert the failure path logs an identifier and a reason, never the record.
- Assert the design and context documents state the immutability implication where a reader would otherwise assume content is editable.

## Acceptance Criteria

- No logging call on any path accepts memory content.
- Error diagnostics are actionable from identifiers alone.
- The immutability consequence is documented at the point of use, not only in this HLD.

## Applies To

All goals; LADR-06. **This NFR constrains but does not solve secret handling** — preventing a secret
from reaching an immutable object is the write path's responsibility, specified in the write-pipeline
HLD, not here.
