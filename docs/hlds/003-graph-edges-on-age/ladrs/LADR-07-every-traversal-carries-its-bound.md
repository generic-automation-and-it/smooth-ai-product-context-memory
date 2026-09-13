# LADR-07: Every traversal carries its bound in the request type

**Status:** Accepted

## Context

The capability this design exists for is provenance reconstruction — the chain from a measurement, to
the finding it produced, to the decision it justified. That is a variable-depth path query, and a
variable-depth path query with no upper bound is the standard way a graph feature becomes a
production incident.

Nothing in the storage layer prevents it. The extension will happily expand a path pattern until it
exhausts the reachable set, and a memory store grows monotonically by design: every capture adds
vertices and edges, and nothing is deleted in normal operation. A query that returns in milliseconds
during prototyping has no mechanism telling it to stop later.

The bound is therefore the application's responsibility, and the question is only where it lives. A
default value on a parameter is the obvious choice and the weakest one: a default is a value the
caller did not think about, and the failure mode of not thinking about a traversal bound is
unbounded traversal.

## Decision

**The depth bound is a `required` property of the query type**, validated to `1..5`, with no
defaulted overload and no code path that constructs a traversal without one.

`MemoryPathQuery.MaxDepth` being `required` means omitting it does not compile. The validator rejects
zero, negatives and anything above five before the request reaches the store.

## Alternatives Considered

- **A default depth** — rejected: it converts a decision the caller must make into one they can skip, which is exactly the hazard.
- **A statement timeout on the graph connection** — rejected as the primary control: it bounds damage rather than preventing it, reports as a timeout rather than as an invalid request, and leaves the caller unable to distinguish a query that was too broad from a database that was too slow. Worth having as defence in depth, not as the bound.
- **Bound by result count rather than depth** — rejected: a `LIMIT` truncates the answer after the traversal has already done the work, so it caps the response size and not the query cost. A limit is applied as well, for the response; it is not the bound.
- **Allow unbounded traversal for an operator or maintenance path** — rejected: there is no such caller, and an escape hatch with no user is an escape hatch that acquires one later without review.

## Consequences

- The store cannot be asked for an unbounded path. Adding an analytics capability that genuinely needs whole-graph reach would be a new, explicitly-argued decision rather than a parameter change.
- Five is a deliberate ceiling, not a measurement: NFR-02 targets depth three, and the two hops of headroom cover a longer provenance chain without opening the shape the bound exists to forbid. Raising it requires a re-measurement, because the plan and the p95 are both depth-sensitive.
- Callers that want the whole neighbourhood of a memory must ask for it one bounded depth at a time, which makes the cost of each request visible at the call site.

## Related

- **README, Guiding Principle** — bounded paths between known endpoints, not whole-graph algorithms.
- **NFR-02** — the depth-3 target this bound brackets.
