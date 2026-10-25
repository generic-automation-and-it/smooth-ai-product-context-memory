# Optional Mimi Knowledge Service planning pack

> Execution status (October 3, 2026): Bob subsequently authorized implementation, GitHub feature-branch publication, bounded real provider calls, and installation/enablement on his existing Unraid stack. Earlier planning-only statements below describe the original document's status. See [EXECUTION.md](EXECUTION.md) for current progress and verified evidence.


Prepared for Bob on October 3, 2026. These documents turn the Jev discussion into a buildable proposal for an optional knowledge service above Mímisbrunnr. The main agent owns the user's task; the service owns knowledge retrieval and maintenance. Existing direct use remains supported.

**Original status:** Planning only. The product decisions below were agreed in conversation. Architecture, names, endpoints, and delivery milestones are proposed implementation choices, not shipped capabilities or approved upstream requirements. Implementation will begin only after Bob's next instruction.

## Which documents these are

| Document | Purpose | Start here when |
|---|---|---|
| [Product requirements](PRD.md) | Defines the problem, benefits, optional modes, behavior, and acceptance criteria | Deciding what to build and why |
| [Technical design](TECHNICAL-DESIGN.md) | Defines the workflow graphs, Jev and LLM responsibilities, contracts, persistence, and integration boundaries | Implementing or reviewing the architecture |
| [Implementation plan](IMPLEMENTATION-PLAN.md) | Breaks delivery into dependency ordered increments with verification and release gates | Starting work on the feature branch |

A **Product Requirements Document**, or PRD, is the right name for the first document. A PRD alone would leave important engineering decisions unresolved, so it is paired with a technical design and implementation plan. Upstream uses BRDs and HLDs for similar purposes. During implementation, the feature was adapted into [BRD-004](../brd/004-optional-knowledge-service/) and [HLD-008](../hlds/008-optional-knowledge-service/), which explicitly document the additive Host contracts. Their status distinguishes practitioner authorization from upstream acceptance and deployment evidence.

## The idea in one paragraph

Give the working agent one skill with two main operations: ask for context and submit learning. An optional Mimi service uses Jev for bounded classifications and routing decisions, a generative LLM for extraction and synthesis, and ordinary code for execution, budgets, validation, and persistence. The service searches the existing corpus, follows relevant relationships, handles duplicate and conflicting claims, and returns cited answers or capture receipts. The working agent supplies task context and evidence without learning Mimi's internal maintenance procedure.

## Agreed decisions

All entries were agreed on October 3, 2026. This is the decision baseline for this feature, rather than a reassessment against the earlier PM product vision.

| ID | Decision | Consequence |
|---|---|---|
| D1 | The main agent remains central to the user interaction | Mimi performs knowledge work; it does not take over the user's task |
| D2 | Move knowledge orchestration into an optional service using Jev and an LLM | The calling agent receives one small workflow skill |
| D3 | Separate interpretation from authority | Confidence may route work but cannot make a suggestion authoritative or prove it shipped |
| D4 | Preserve supported information automatically | Store proposals and unresolved conflicts with their evidence; promotion requires sufficient authority |
| D5 | Assess coverage against the request | Return supported answers, conflicts, gaps, and unexamined areas without claiming global completeness |
| D6 | Enforce a shared processing budget | Expansion, parallel workers, retries, and stronger models consume the same request allowance |
| D7 | Retrieve more for missing evidence and reason more for difficult evidence | An expensive model is not a substitute for absent sources |
| D8 | Accept evidence rich handoffs | The caller supplies attributed conversation material and references; extraction and deduplication belong to Mimi |
| D9 | Separate durable receipt from completed incorporation | Acknowledged capture survives restart; retries do not create duplicate knowledge |
| D10 | Accept hosted Jev and LLM processing; remove credentials and secrets before sending or storing | No per-project provider permission framework, no rigid context allocation rules, and no approval for each model call |
| D11 | Preserve direct-agent mode | The service is optional, disabled by default, and shares the existing corpus |
| D12 | Build on a feature branch in increments | No implementation on main and no default deployment of an unfinished service |

## Expected benefits and limits

The primary benefits are less knowledge-management instruction in the main agent, less raw retrieval material in its conversation, and a consistent maintenance workflow independent of which agent is doing the user's work. Central execution also makes it possible to inspect routing, repeat evaluations, enforce budgets, and fix a retrieval problem once.

Lower total cost, fewer duplicates, and improved recall are hypotheses to measure. Jev's price does not establish the cost of the complete workflow. Model reasoning still occurs, and evidence can still be missed or misinterpreted. The PRD specifies how these benefits will be assessed.

## Source baseline

The application baseline inspected for this pack is upstream main **bbc85530a1ffb0f3d3ec4d1799dba6c2bbcfd9dc**, verified October 3, 2026. Recheck upstream before implementation. The earlier product foundation (private workspace historical context) remains historical context; the October 3 decisions above define this feature. No private product corpus was copied into this pack.

Primary references:

- [Mimi source at the inspected revision](https://github.com/generic-automation-and-it/smooth-ai-product-context-memory/tree/bbc85530a1ffb0f3d3ec4d1799dba6c2bbcfd9dc).
- [Mimi capture skill](https://github.com/generic-automation-and-it/smooth-ai-product-context-memory/blob/bbc8553/.agents/skills/mimisbrunnr-odin-context-memory/SKILL.md): current agent-owned judgment and supported write procedure.
- [Mimi memory model](https://github.com/generic-automation-and-it/smooth-ai-product-context-memory/blob/bbc8553/src/SmoothAiProductContextMemory.Domain/Entities/MemoryVersion.cs): current versions, statuses, sources, and bodies.
- [TypeSafe introduction](https://docs.typesafe.ai/introduction): Jev's Choice, Score, and Noul primitives; structured decisions rather than generated prose.
- [TypeSafe intent routing](https://docs.typesafe.ai/patterns/intent-routing) and [confidence](https://docs.typesafe.ai/confidence): routing patterns and the meaning of confidence.
- [Workflow and agent patterns](https://docs.langchain.com/oss/python/langgraph/workflows-agents): routing, parallel work, and evaluation loops. This is a conceptual reference, not a requirement to adopt LangGraph.

## Where these documents live

This pack began as local planning documentation and now accompanies the authorized isolated feature implementation. EXECUTION.md records actual delivery status; planning approval does not claim deployment completion. The mandatory October 3 collaborator amendment is recorded in COLLABORATOR-REVIEW.md and integrated into the PRD, technical design and implementation plan.
