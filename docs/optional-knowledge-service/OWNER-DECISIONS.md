# Owner-decision trace for identity and approval

This feature does not resolve upstream owner decisions by inference. The following existing records were inspected before finalizing its evidence model.

| Existing record | What it establishes | Optional-service consequence |
|---|---|---|
| [Source README, Where Mímisbrunnr loses](../../README.md#where-mímisbrunnr-loses) | Untracked capture identity and acceptance of designs over draft children are explicitly listed as open owner decisions | Do not label either global policy settled by this feature |
| [Direct capture skill](../../.agents/skills/mimisbrunnr-odin-context-memory/SKILL.md) | Current direct workflow assigns a synthetic `local:<guid>` ticket for untracked work; memory identity remains scoped to its group | Request/message namespaces prevent technical retry collisions; they do not replace memory identity or authorize cross-group merging |
| [Snapshot HLD context](../hlds/006-corpus-snapshot-and-restore/AGENTS.md) | The parent design is accepted while its LADRs/NFRs remain Draft; parent acceptance is expressly not acceptance of each child | Preserve document-specific approval and child status; do not infer blanket acceptance or deployment |
| [Graph HLD](../hlds/003-graph-edges-on-age/README.md) | Design acceptance, implementation acceptance and release gates are stated separately | A document approval event is not runtime proof |
| [Write pipeline approval decision](../hlds/002-context-memory-write-pipeline/ladrs/LADR-06-gate-status-not-persistence.md) | Approval gates status, rather than whether supported evidence can be preserved | Preserve proposals and disagreements without silently promoting them |

The feature's technical identity has three levels: caller task/conversation namespace, stable source message identity, and idempotent operation identity. Repeated unchanged submissions retain identity; a distinct task may reuse a short message ID without colliding. Changed source text must not silently replace immutable earlier evidence. Core memory `(group, uuid)` identity and version history remain unchanged.

Evidence must distinguish approval of a particular document, approval of an intended product change, and affirmative observation that behavior was deployed. An approved PRD may contain draft alternatives or future plans. A high model confidence, an accepted parent design, a committed source file or an approved future decision alone is not evidence of shipped product behavior. Keep source attribution, dates, applicability and unresolved authority visible.

Implementation and independent tests must enforce these distinctions; this trace is not an acceptance certificate. [COLLABORATOR-REVIEW.md](COLLABORATOR-REVIEW.md) records their verification status.
